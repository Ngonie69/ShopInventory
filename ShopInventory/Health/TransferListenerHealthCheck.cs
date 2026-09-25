using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.Health;

/// <summary>
/// Reports whether TransferEventListener is still reading SAP.
/// </summary>
/// <remarks>
/// This is the only thing in either system that notices the listener has stopped. The failure it
/// catches is quiet by construction: the listener's poll loop swallows its own exceptions and leaves
/// the window unadvanced, so it keeps answering HTTP from memory while nothing new is read; and on
/// this side the webhook it feeds is fire-and-forget, so its absence looks exactly like an afternoon
/// with no transfers in it. Nothing else raises an alert, and the damage lands on the till — every
/// transfer made while the listener is down is missing from the day's snapshot, so the shop can see
/// stock on the shelf that the screen says it does not have.
///
/// The probe reads the listener's own poll status rather than SAP, so it costs one local HTTP call
/// and cannot consume a Service Layer session.
///
/// <c>Degraded</c> rather than <c>Unhealthy</c> is used for a poll that is merely late, because the
/// listener re-reads the same window next cycle: a late poll loses nothing once it recovers. What
/// earns <c>Unhealthy</c> is the state where movements are missing from the tills now and nothing is
/// catching up — a listener that is not polling at all, or lines that have waited past the critical
/// threshold. Lines this API refused or the listener gave up on are not retried either, but the next
/// morning's snapshot counts them, so they are <c>Degraded</c>.
/// </remarks>
public sealed class TransferListenerHealthCheck(
    ITransferEventListenerClient listenerClient,
    IOptions<TransferEventListenerSettings> listenerOptions,
    IOptions<DailyStockSettings> dailyStockOptions) : IHealthCheck
{
    private readonly TransferEventListenerSettings _settings = listenerOptions.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!listenerClient.IsEnabled)
        {
            return HealthCheckResult.Healthy(
                "The TransferEventListener integration is switched off, so nothing is expected of it.");
        }

        DTOs.TransferListenerHealthDto health;
        try
        {
            health = await listenerClient.GetHealthAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"TransferEventListener at {listenerClient.BaseUrl} is unreachable, so no stock transfer "
                + "is reaching the daily snapshot and nothing else will report it: "
                + ex.Message,
                ex,
                new Dictionary<string, object>
                {
                    ["baseUrl"] = listenerClient.BaseUrl,
                    ["reachable"] = false
                });
        }

        var poll = health.Poll ?? new DTOs.TransferListenerPollDto();

        // Measured from the last cycle that actually read SAP. Before the first one lands there is no
        // such moment, so the listener's process start stands in — a listener that has never succeeded
        // has been failing since it started.
        var reference = poll.LastSuccessUtc ?? poll.ProcessStartedUtc;
        var sinceSuccess = DateTime.UtcNow - reference;

        var data = new Dictionary<string, object>
        {
            ["baseUrl"] = listenerClient.BaseUrl,
            ["reachable"] = true,
            ["listenerStatus"] = health.Status ?? string.Empty,
            ["minutesSinceSuccessfulPoll"] = Math.Round(sinceSuccess.TotalMinutes, 0),
            ["consecutiveFailures"] = poll.ConsecutiveFailures,
            ["webhookFailures"] = poll.WebhookFailures,
            ["documentsSeen"] = health.DocumentsSeen,
            ["pollIntervalSeconds"] = poll.PollIntervalSeconds,
            ["lastError"] = poll.LastError ?? string.Empty,
            ["pendingNotifications"] = poll.PendingNotifications,
            ["abandonedNotifications"] = poll.AbandonedNotifications,
            ["rejectedNotifications"] = poll.RejectedNotifications,
            ["lastDeliveryError"] = poll.LastDeliveryError ?? string.Empty
        };

        var unwatched = await UnwatchedWarehousesAsync(cancellationToken);
        if (unwatched.Count > 0)
        {
            data["warehousesNotWatchedByListener"] = string.Join(", ", unwatched);
        }

        if (!poll.PollingStarted)
        {
            return HealthCheckResult.Unhealthy(
                "TransferEventListener answers but its SAP poll loop has not started, so no transfer is "
                + "being read at all. " + (poll.LastError ?? string.Empty).TrimEnd(),
                data: data);
        }

        var critical = TimeSpan.FromMinutes(Math.Max(_settings.PollStalenessCriticalMinutes, 1));
        var warning = TimeSpan.FromMinutes(Math.Max(_settings.PollStalenessWarningMinutes, 1));

        if (sinceSuccess >= critical)
        {
            return HealthCheckResult.Unhealthy(
                $"TransferEventListener has not polled SAP successfully for {sinceSuccess.TotalMinutes:N0} "
                + $"minute(s) ({poll.ConsecutiveFailures} consecutive failure(s)). Transfers made since then "
                + "are absent from the daily snapshot, so a till will refuse stock the warehouse holds. "
                + (poll.LastError ?? string.Empty).TrimEnd(),
                data: data);
        }

        // Reading SAP worked and delivering to this API did not. On 2026-09-17 the listener posted every
        // line for a day to a port nothing listened on while this check reported Healthy, because it
        // only ever asked about the poll. The lines are retried, so a short wait is only late — but
        // one that has waited past the critical threshold is stock a till is refusing right now.
        if (poll.PendingNotifications > 0 && poll.OldestPendingNotificationUtc is { } oldestPending)
        {
            var waited = DateTime.UtcNow - DateTime.SpecifyKind(oldestPending, DateTimeKind.Utc);
            var cause = string.IsNullOrWhiteSpace(poll.LastDeliveryError)
                ? string.Empty
                : $" Last answer: {poll.LastDeliveryError.Trim()}"
                  + (string.IsNullOrWhiteSpace(poll.WebhookUrl) ? "." : $" from {poll.WebhookUrl}.");

            if (waited >= critical)
            {
                return HealthCheckResult.Unhealthy(
                    $"{poll.PendingNotifications} transfer line(s) found in SAP have not reached this API's "
                    + $"stock ledger, the oldest for {waited.TotalMinutes:N0} minute(s). Those movements are "
                    + "missing from local stock and the tills." + cause,
                    data: data);
            }

            if (waited >= warning)
            {
                return HealthCheckResult.Degraded(
                    $"{poll.PendingNotifications} transfer line(s) are waiting to reach this API's stock "
                    + $"ledger, the oldest for {waited.TotalMinutes:N0} minute(s). They are retried every "
                    + "cycle." + cause,
                    data: data);
            }
        }

        // Documents with a line this API refused, or that the listener gave up on. Nothing retries them,
        // but none is lost for good: the next morning's snapshot reads SAP afresh and counts them, and a
        // line given up on because its ledger day ended is already in the snapshot that ended that day.
        // So this is Degraded by the rule above. The count is also held since the listener started, so
        // it cannot say whether the damage is still on today's tills.
        if (poll.WebhookFailures > 0)
        {
            return HealthCheckResult.Degraded(
                $"{poll.WebhookFailures} transfer document(s) have a line this API will never take from the "
                + $"listener ({poll.RejectedNotifications} line(s) refused as invalid, {poll.AbandonedNotifications} "
                + "given up on), and nothing retries them. A refused line, or one dropped from a full retry "
                + "queue, is missing from local stock until the next morning's snapshot; one given up on when "
                + "its ledger day ended is already counted by the snapshot that ended it. Counted since the "
                + $"listener started at {poll.ProcessStartedUtc:yyyy-MM-dd HH:mm} UTC.",
                data: data);
        }

        if (sinceSuccess >= warning || poll.ConsecutiveFailures > 0)
        {
            return HealthCheckResult.Degraded(
                $"TransferEventListener last polled SAP successfully {sinceSuccess.TotalMinutes:N0} minute(s) "
                + $"ago ({poll.ConsecutiveFailures} consecutive failure(s)). The window is re-read next cycle, "
                + "so nothing is lost yet. " + (poll.LastError ?? string.Empty).TrimEnd(),
                data: data);
        }

        if (unwatched.Count > 0)
        {
            return HealthCheckResult.Degraded(
                $"TransferEventListener is polling normally, but {unwatched.Count} warehouse(s) in this API's "
                + $"daily snapshot are not on its watch list: {string.Join(", ", unwatched)}. Those warehouses "
                + "receive no transfer adjustments, so their snapshot drifts from SAP over the day.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            $"TransferEventListener polled SAP successfully {sinceSuccess.TotalMinutes:N0} minute(s) ago.",
            data);
    }

    /// <summary>
    /// Warehouses this API snapshots that the listener does not raise events for.
    /// </summary>
    /// <remarks>
    /// The two lists are maintained independently — one in this API's configuration, the other
    /// compiled into the listener's <c>WarehouseConfig</c> — so they drift silently, and the drift is
    /// invisible from either side. A warehouse on this list is snapshotted at 07:00 and then never
    /// adjusted again, which reads as an ordinary quiet day rather than a gap.
    /// </remarks>
    private async Task<List<string>> UnwatchedWarehousesAsync(CancellationToken cancellationToken)
    {
        var snapshotted = dailyStockOptions.Value.MonitoredWarehouses;
        if (snapshotted.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string> watched;
        try
        {
            watched = await listenerClient.GetMonitoredWarehousesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Swallowed on purpose: this is a secondary observation, and failing it must not mask the
            // poll status the caller actually came for — which was read successfully to get here.
            return [];
        }

        var watchedSet = watched.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Distinct because the configured list is not guaranteed to be one — see the note in
        // GetTransferListenerStatusHandler. A doubled entry would double the count in the message.
        return snapshotted
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse))
            .Select(warehouse => warehouse.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(warehouse => !watchedSet.Contains(warehouse))
            .OrderBy(warehouse => warehouse, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
