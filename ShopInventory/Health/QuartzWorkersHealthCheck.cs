using Microsoft.Extensions.Diagnostics.HealthChecks;
using Quartz;
using Quartz.Impl.Matchers;

namespace ShopInventory.Health;

/// <summary>
/// Health check for the Quartz-hosted background jobs. Replaces the hand-rolled
/// BackgroundWorkersHealthCheck: instead of reading a custom cluster-state table it inspects the
/// scheduler directly. The scheduler must be started (not in standby), and no job trigger may be
/// in an ERROR state or left unscheduled (null next-fire time). A BLOCKED trigger is expected when
/// another clustered node owns a [DisallowConcurrentExecution] job during blue/green overlap.
/// Trigger state is held in
/// the clustered Postgres job store, so this reflects the whole cluster, not just this node.
/// Registered under the "workers" name so the existing alert email continues to surface it.
/// </summary>
public sealed class QuartzWorkersHealthCheck(
    ISchedulerFactory schedulerFactory,
    ILogger<QuartzWorkersHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);

        if (scheduler.IsShutdown || !scheduler.IsStarted || scheduler.InStandbyMode)
        {
            return HealthCheckResult.Unhealthy("Quartz scheduler is not running (shut down or in standby).");
        }

        var triggerKeys = await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup(), cancellationToken);
        if (triggerKeys.Count == 0)
        {
            return HealthCheckResult.Unhealthy("No background job triggers are scheduled.");
        }

        var snapshots = new List<TriggerSnapshot>();
        foreach (var key in triggerKeys)
        {
            var state = await scheduler.GetTriggerState(key, cancellationToken);
            var trigger = await scheduler.GetTrigger(key, cancellationToken);
            snapshots.Add(new TriggerSnapshot(
                key,
                trigger?.JobKey,
                state,
                trigger?.GetPreviousFireTimeUtc(),
                trigger?.GetNextFireTimeUtc()));
        }

        var (failures, data) = Evaluate(snapshots);

        if (failures.Count > 0)
        {
            var detail = string.Join(" ", failures);
            logger.LogWarning(
                "Quartz background job health is Unhealthy. Failing: {Failures} | Triggers: {Triggers}",
                detail,
                string.Join(" || ", data.Select(kv => $"{kv.Key}|{kv.Value}")));
            return HealthCheckResult.Unhealthy($"Background jobs unhealthy: {detail}", data: data);
        }

        return HealthCheckResult.Healthy("All background jobs are scheduled and none are in an error state.", data);
    }

    internal sealed record TriggerSnapshot(
        TriggerKey Key,
        JobKey? JobKey,
        TriggerState State,
        DateTimeOffset? PreviousFireUtc,
        DateTimeOffset? NextFireUtc);

    /// <summary>
    /// Keyed by trigger, not job. A job can carry more than one trigger — daily-incoming-payment has
    /// its 17:00 cron and a retry interval on the same key — and keying by job let a healthy
    /// trigger's row overwrite a broken one's, so the report said the job was failing while every
    /// row it printed was Normal.
    /// </summary>
    internal static (List<string> Failures, Dictionary<string, object> Data) Evaluate(
        IEnumerable<TriggerSnapshot> triggers)
    {
        var failures = new List<string>();
        var data = new Dictionary<string, object>();

        foreach (var trigger in triggers.OrderBy(t => t.Key.Group).ThenBy(t => t.Key.Name))
        {
            var triggerName = trigger.Key.Group == SchedulerConstants.DefaultGroup
                ? trigger.Key.Name
                : $"{trigger.Key.Group}.{trigger.Key.Name}";
            var label = trigger.JobKey is null || trigger.JobKey.Name == trigger.Key.Name
                ? triggerName
                : $"{triggerName} (job {trigger.JobKey.Name})";

            data[triggerName] = $"job={trigger.JobKey?.Name ?? "none"}; state={trigger.State}; prevFire={trigger.PreviousFireUtc?.ToString("O") ?? "none"}; nextFire={trigger.NextFireUtc?.ToString("O") ?? "none"}";

            // BLOCKED is a normal clustered state while another node owns a non-concurrent job.
            // Treat it as unhealthy only if it also has no future fire time; the next-fire check
            // below catches that misconfiguration while allowing blue/green cutovers to proceed.
            if (trigger.State == TriggerState.Error)
            {
                failures.Add($"{label} trigger is in {trigger.State} state.");
            }
            else if (trigger.NextFireUtc is null && trigger.State != TriggerState.Complete)
            {
                failures.Add($"{label} trigger has no next fire time (paused or misconfigured).");
            }
        }

        return (failures, data);
    }
}
