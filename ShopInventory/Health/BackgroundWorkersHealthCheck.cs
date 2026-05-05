using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Health;

public sealed class BackgroundWorkersHealthCheck(
    IServiceScopeFactory scopeFactory,
    BackgroundWorkerHealthRegistry healthRegistry) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var utcNow = DateTime.UtcNow;
        var requiredWorkers = healthRegistry.GetSnapshots()
            .Where(worker => worker.IsCritical)
            .OrderBy(worker => worker.WorkerName, StringComparer.Ordinal)
            .ToList();

        if (requiredWorkers.Count == 0)
        {
            return HealthCheckResult.Unhealthy("No critical background workers are registered locally.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var requiredWorkerNames = requiredWorkers.Select(worker => worker.WorkerName).ToArray();
        var clusterStates = await dbContext.BackgroundWorkerClusterStates
            .AsNoTracking()
            .Where(state => state.IsCritical && requiredWorkerNames.Contains(state.WorkerName))
            .OrderBy(state => state.WorkerName)
            .ThenBy(state => state.InstanceId)
            .ToListAsync(cancellationToken);

        var failures = new List<string>();
        foreach (var worker in requiredWorkers)
        {
            var workerStates = clusterStates
                .Where(state => string.Equals(state.WorkerName, worker.WorkerName, StringComparison.Ordinal))
                .ToList();

            if (workerStates.Count == 0)
            {
                failures.Add($"{worker.WorkerName} has no cluster heartbeat rows.");
                continue;
            }

            var healthyLeader = workerStates.FirstOrDefault(state =>
                string.Equals(state.Mode, "Leader", StringComparison.OrdinalIgnoreCase) &&
                utcNow - state.LastHeartbeatUtc <= TimeSpan.FromSeconds(state.HealthyWindowSeconds) &&
                !HasBlockingFailures(state));

            if (healthyLeader is null)
            {
                var newestState = workerStates
                    .OrderByDescending(state => state.LastHeartbeatUtc)
                    .First();
                var heartbeatAge = utcNow - newestState.LastHeartbeatUtc;
                failures.Add($"{worker.WorkerName} has no healthy cluster leader. Latest mode={newestState.Mode}, age={heartbeatAge.TotalSeconds:N0}s.");
            }
        }

        var data = new Dictionary<string, object>
        {
            ["workers"] = clusterStates.Select(worker =>
                $"{worker.WorkerName}|instance={worker.InstanceId}|mode={worker.Mode}|heartbeat={worker.LastHeartbeatUtc:O}|success={(worker.LastSuccessfulRunUtc.HasValue ? worker.LastSuccessfulRunUtc.Value.ToString("O") : "none")}|failures={worker.ConsecutiveFailures}")
                .ToArray()
        };

        if (failures.Count > 0)
        {
            data["unhealthyWorkers"] = failures.ToArray();
            return HealthCheckResult.Unhealthy("Critical background workers have no healthy cluster leader.", data: data);
        }

        return HealthCheckResult.Healthy("Critical background workers have healthy cluster leaders.", data);
    }

    private static bool HasBlockingFailures(Models.Entities.BackgroundWorkerClusterStateEntity state)
    {
        return state.ConsecutiveFailures >= 3 &&
               state.LastFailureUtc.HasValue &&
               (!state.LastSuccessfulRunUtc.HasValue || state.LastFailureUtc.Value >= state.LastSuccessfulRunUtc.Value);
    }
}