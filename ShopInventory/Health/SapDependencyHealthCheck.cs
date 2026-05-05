using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Services;

namespace ShopInventory.Health;

public sealed class SapDependencyHealthCheck(
    IServiceScopeFactory scopeFactory,
    SapCircuitBreakerState circuitBreakerState) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var circuitSnapshot = circuitBreakerState.GetSnapshot();
        if (!circuitSnapshot.IsEnabled)
        {
            return HealthCheckResult.Healthy("SAP dependency is disabled.");
        }

        if (circuitSnapshot.IsOpen)
        {
            return HealthCheckResult.Unhealthy(
                "SAP dependency circuit breaker is open.",
                data: new Dictionary<string, object>
                {
                    ["consecutiveFailures"] = circuitSnapshot.ConsecutiveFailures,
                    ["lastFailure"] = circuitSnapshot.LastFailure ?? string.Empty,
                    ["lastFailureAtUtc"] = circuitSnapshot.LastFailureUtc?.ToString("O") ?? string.Empty,
                    ["openUntilUtc"] = circuitSnapshot.OpenUntilUtc?.ToString("O") ?? string.Empty
                });
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var syncStatusService = scope.ServiceProvider.GetRequiredService<ISyncStatusService>();

        var connectionStatus = await syncStatusService.CheckSapConnectionAsync(cancellationToken);
        if (!connectionStatus.IsConnected)
        {
            return HealthCheckResult.Unhealthy(
                connectionStatus.LastError ?? "SAP dependency is unreachable.",
                data: new Dictionary<string, object>
                {
                    ["companyDb"] = connectionStatus.CompanyDb ?? string.Empty,
                    ["consecutiveFailures"] = connectionStatus.ConsecutiveFailures,
                    ["lastErrorAtUtc"] = connectionStatus.LastErrorAt?.ToString("O") ?? string.Empty,
                    ["circuitOpen"] = circuitSnapshot.IsOpen
                });
        }

        return HealthCheckResult.Healthy(
            "SAP dependency is reachable.",
            data: new Dictionary<string, object>
            {
                ["companyDb"] = connectionStatus.CompanyDb ?? string.Empty,
                ["responseTimeMs"] = connectionStatus.ResponseTimeMs ?? 0L,
                ["consecutiveFailures"] = circuitSnapshot.ConsecutiveFailures
            });
    }
}