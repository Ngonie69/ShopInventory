using Microsoft.Extensions.Diagnostics.HealthChecks;
using Quartz;
using Quartz.Impl.Matchers;

namespace ShopInventory.Web.Health;

public sealed class WebQuartzWorkersHealthCheck(ISchedulerFactory schedulerFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);

        if (scheduler.IsShutdown || !scheduler.IsStarted || scheduler.InStandbyMode)
        {
            return HealthCheckResult.Unhealthy("Web Quartz scheduler is not running.");
        }

        var triggerKeys = await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup(), cancellationToken);
        if (triggerKeys.Count == 0)
        {
            return HealthCheckResult.Unhealthy("No web background job triggers are scheduled.");
        }

        var failures = new List<string>();
        var data = new Dictionary<string, object>();

        foreach (var key in triggerKeys.OrderBy(key => key.Group).ThenBy(key => key.Name))
        {
            var state = await scheduler.GetTriggerState(key, cancellationToken);
            var trigger = await scheduler.GetTrigger(key, cancellationToken);
            var dataKey = $"{key.Group}.{key.Name}";

            data[$"{dataKey}.state"] = state.ToString();
            data[$"{dataKey}.nextFireUtc"] = trigger?.GetNextFireTimeUtc()?.ToString("O") ?? "";

            if (trigger is null)
            {
                failures.Add($"{dataKey} is missing.");
                continue;
            }

            if (trigger.GetNextFireTimeUtc() is null)
            {
                failures.Add($"{dataKey} has no next fire time.");
            }

            if (state is TriggerState.Error or TriggerState.Blocked)
            {
                failures.Add($"{dataKey} is {state}.");
            }
        }

        return failures.Count == 0
            ? HealthCheckResult.Healthy("Web Quartz background jobs are scheduled.", data)
            : HealthCheckResult.Unhealthy(
                $"Web Quartz background jobs are not healthy: {string.Join(" ", failures)}",
                data: data);
    }
}
