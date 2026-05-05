using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Services;

namespace ShopInventory.Health;

public sealed class OperationalSyncHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var syncStatusService = scope.ServiceProvider.GetRequiredService<ISyncStatusService>();

        var healthSummary = await syncStatusService.GetHealthSummaryAsync(cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["overallHealth"] = healthSummary.OverallHealth,
            ["healthScore"] = healthSummary.HealthScore,
            ["issues"] = healthSummary.Issues.ToArray(),
            ["recommendations"] = healthSummary.Recommendations.ToArray()
        };

        return healthSummary.OverallHealth switch
        {
            "Critical" => HealthCheckResult.Unhealthy("Operational sync health is critical.", data: data),
            "Warning" => HealthCheckResult.Degraded("Operational sync health is degraded.", data: data),
            _ => HealthCheckResult.Healthy("Operational sync health is healthy.", data)
        };
    }
}