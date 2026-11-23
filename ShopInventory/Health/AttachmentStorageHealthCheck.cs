using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ShopInventory.Health;

/// <summary>
/// Reports whether uploads can be written to the attachment store, from the test writes
/// <see cref="AttachmentStorageMonitor"/> makes once a minute.
/// </summary>
/// <remarks>
/// <para>
/// Registered under <c>dependencies</c> only, like the SAP check. That puts it in the alert job and on
/// the Web dashboard. A dead share stops uploads and nothing else, so it must not take the API out of
/// rotation or block a deployment, which is what <c>ready</c> and <c>deploy-ready</c> would do.
/// </para>
/// <para>
/// One failed write is <c>Degraded</c>, and two in a row are <c>Unhealthy</c>. The alert job mails
/// <c>Unhealthy</c> at once, and waits for <c>Degraded</c> to hold for two polls. A single blip on the
/// share therefore passes without a mail. A real outage fails two writes inside two minutes, so the
/// next poll of the alert job, at most five minutes later, sends the mail. A write still waiting
/// after <see cref="NoAnswerAfter"/> counts as one more failure. That is how a share that never
/// answers shows up: on 2026-11-23 each upload took 21 to 64 seconds to fail.
/// </para>
/// </remarks>
public sealed class AttachmentStorageHealthCheck(
    AttachmentStorageMonitor monitor,
    TimeProvider timeProvider) : IHealthCheck
{
    /// <summary>A healthy write takes milliseconds, so one that has run this long has not been answered.</summary>
    public static readonly TimeSpan NoAnswerAfter = TimeSpan.FromSeconds(15);

    private const string Consequence =
        " Every POD and crate POD upload fails while this lasts. Handsets are told to send again, so the photos wait on the phones.";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(monitor.Current, timeProvider.GetUtcNow()));

    public static HealthCheckResult Evaluate(AttachmentStorageSnapshot snapshot, DateTimeOffset now)
    {
        var last = snapshot.LastResult;
        var running = snapshot.ProbeStartedAt is { } startedAt ? now - startedAt : (TimeSpan?)null;
        var unanswered = running >= NoAnswerAfter;
        var failures = snapshot.ConsecutiveFailures + (unanswered ? 1 : 0);
        var failingStatus = failures >= 2 ? HealthStatus.Unhealthy : HealthStatus.Degraded;

        var data = new Dictionary<string, object>
        {
            ["consecutiveFailures"] = failures
        };
        if (snapshot.LastStoredAt is { } lastStored)
        {
            data["lastStoredUtc"] = lastStored.UtcDateTime;
        }
        if (last is not null)
        {
            data["lastProbeUtc"] = last.FinishedAt.UtcDateTime;
            data["probeMilliseconds"] = Math.Round(last.Duration.TotalMilliseconds);
            data["foldersChecked"] = last.FoldersChecked;
        }
        if (last is { Succeeded: false, Error: { } error })
        {
            data["failedFolder"] = last.FailedFolder ?? string.Empty;
            data["failedStage"] = last.FailedStage?.ToString() ?? string.Empty;
            data["error"] = AttachmentStorageMonitor.DescribeError(error);
        }
        if (unanswered)
        {
            data["probeRunningSeconds"] = Math.Round(running!.Value.TotalSeconds);
        }

        var since = snapshot.LastStoredAt is { } stored
            ? $"since {stored.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
            : "since start-up";

        if (unanswered)
        {
            var before = last is { Stored: false, Error: { } previousError }
                ? $" The write before it failed in {last.FailedFolder}: {AttachmentStorageMonitor.DescribeError(previousError)}"
                : string.Empty;

            return new HealthCheckResult(
                failingStatus,
                $"A test write to the attachment store started {running!.Value.TotalSeconds:N0} s ago and has not "
                + "come back. A network share that cannot be reached takes this long to fail, and an upload "
                + $"waits just as long.{before} No write has reached the store {since}.{Consequence}",
                data: data);
        }

        if (last is null)
        {
            return HealthCheckResult.Healthy(
                "The attachment store has not been tested yet. The first test write runs at start-up.",
                data);
        }

        if (!last.Stored)
        {
            return new HealthCheckResult(
                failingStatus,
                $"The attachment store refused a test write in {last.FailedFolder}: "
                + $"{AttachmentStorageMonitor.DescribeError(last.Error!)} {failures} test write(s) in a row have "
                + $"failed, and none has reached the store {since}.{Consequence}",
                last.Error,
                data);
        }

        if (!last.Succeeded)
        {
            return HealthCheckResult.Degraded(
                $"The attachment store took a test write in {last.FailedFolder} but would not delete it: "
                + $"{AttachmentStorageMonitor.DescribeError(last.Error!)} Uploads still work.",
                last.Error,
                data);
        }

        return HealthCheckResult.Healthy(
            $"The attachment store took a test write in {last.FoldersChecked} folder(s) "
            + $"{(now - last.FinishedAt).TotalSeconds:N0} s ago, in {last.Duration.TotalMilliseconds:N0} ms.",
            data);
    }
}
