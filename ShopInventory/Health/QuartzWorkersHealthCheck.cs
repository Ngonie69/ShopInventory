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

        var failures = new List<string>();
        var data = new Dictionary<string, object>();

        foreach (var key in triggerKeys)
        {
            var state = await scheduler.GetTriggerState(key, cancellationToken);
            var trigger = await scheduler.GetTrigger(key, cancellationToken);
            var jobName = trigger?.JobKey.Name ?? key.Name;
            var nextFire = trigger?.GetNextFireTimeUtc();

            data[jobName] = $"state={state}; prevFire={trigger?.GetPreviousFireTimeUtc()?.ToString("O") ?? "none"}; nextFire={nextFire?.ToString("O") ?? "none"}";

            // BLOCKED is a normal clustered state while another node owns a non-concurrent job.
            // Treat it as unhealthy only if it also has no future fire time; the next-fire check
            // below catches that misconfiguration while allowing blue/green cutovers to proceed.
            if (state == TriggerState.Error)
            {
                failures.Add($"{jobName} trigger is in {state} state.");
            }
            else if (nextFire is null && state != TriggerState.Complete)
            {
                failures.Add($"{jobName} trigger has no next fire time (paused or misconfigured).");
            }
        }

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
}
