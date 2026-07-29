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

        var issueSummary = healthSummary.Issues.Count > 0
            ? string.Join("; ", healthSummary.Issues)
            : "no detailed issues were reported";

        return healthSummary.OverallHealth switch
        {
            "Critical" => HealthCheckResult.Unhealthy(
                $"Operational sync health is critical (score {healthSummary.HealthScore}): {issueSummary}.",
                data: data),
            "Warning" => HealthCheckResult.Degraded(
                $"Operational sync health is degraded (score {healthSummary.HealthScore}): {issueSummary}.",
                data: data),
            _ => HealthCheckResult.Healthy("Operational sync health is healthy.", data)
        };
    }
}
