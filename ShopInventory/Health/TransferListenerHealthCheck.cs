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
/// earns <c>Unhealthy</c> is the state where recovery no longer helps — a listener that is not
/// polling at all, or webhook deliveries that failed and are never retried.
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
            ["lastError"] = poll.LastError ?? string.Empty
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

        // Detection worked and delivery did not, and nothing retries a failed webhook: those documents
        // never reach the snapshot however healthy the listener becomes afterwards.
        if (poll.WebhookFailures > 0)
        {
            return HealthCheckResult.Unhealthy(
                $"{poll.WebhookFailures} transfer document(s) were detected but their webhook to this API "
                + "failed, and nothing retries them. Their stock movements are missing from the daily "
                + "snapshot and have to be applied by re-running the morning fetch.",
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

        return snapshotted
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse))
            .Where(warehouse => !watchedSet.Contains(warehouse.Trim()))
            .OrderBy(warehouse => warehouse, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
