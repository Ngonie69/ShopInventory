using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Services;
using ShopInventory.Services.SlHealth;

namespace ShopInventory.Health;

public sealed class SapDependencyHealthCheck(
    IServiceScopeFactory scopeFactory,
    SapCircuitBreakerState circuitBreakerState,
    SapAvailability? availability = null,
    SlHealthMonitor? slHealth = null) : IHealthCheck
{
    /// <summary>
    /// SAP's state as this API finds it, with SL Health's view of the same Service Layer beside it in the
    /// data when there is one. SL Health never changes the status: that stays this API's own finding.
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = await CheckSapAsync(cancellationToken);

        if (slHealth?.Current is not { } status)
        {
            return result;
        }

        var data = new Dictionary<string, object>(result.Data)
        {
            ["slHealthLevel"] = status.Level ?? string.Empty,
            ["slHealthAdvice"] = status.AdviceValue.ToString(),
            ["slHealthReasons"] = (status.Reasons ?? []).ToArray()
        };

        if (slHealth.ExplanationForRecord is { } explanation)
        {
            data["slHealthExplanation"] = explanation;
        }

        return new HealthCheckResult(result.Status, result.Description, result.Exception, data);
    }

    private async Task<HealthCheckResult> CheckSapAsync(CancellationToken cancellationToken)
    {
        var circuitSnapshot = circuitBreakerState.GetSnapshot();
        if (!circuitSnapshot.IsEnabled)
        {
            return HealthCheckResult.Healthy("SAP dependency is disabled.");
        }

        // Turned off on purpose, and the same on every node, so it must not take nodes out of rotation.
        if (circuitSnapshot.IsSwitchedOff)
        {
            return HealthCheckResult.Degraded("SAP connection is turned off in Settings.");
        }

        // Declared by the probe for the whole cluster, so this node's breaker may well be closed.
        if (availability?.Current is { InOutage: true } outage)
        {
            return HealthCheckResult.Unhealthy(
                "SAP is unavailable (declared outage).",
                data: new Dictionary<string, object>
                {
                    ["outageId"] = outage.OutageId ?? 0,
                    ["sinceUtc"] = outage.SinceUtc?.ToString("O") ?? string.Empty,
                    ["cause"] = outage.Cause ?? string.Empty
                });
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