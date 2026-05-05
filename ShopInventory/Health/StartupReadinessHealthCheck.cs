using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ShopInventory.Health;

public sealed class StartupReadinessHealthCheck(StartupReadinessSignal readinessSignal) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (readinessSignal.IsReady)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Startup initialization completed."));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy(
            readinessSignal.FailureReason ?? "Application startup is still in progress."));
    }
}