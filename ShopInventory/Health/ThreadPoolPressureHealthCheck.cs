using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ShopInventory.Health;

public sealed class ThreadPoolPressureHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ThreadPool.GetAvailableThreads(out var availableWorkerThreads, out var availableIoThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxIoThreads);
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minIoThreads);

        var usedWorkerThreads = maxWorkerThreads - availableWorkerThreads;
        var workerUtilization = maxWorkerThreads == 0
            ? 0d
            : (double)usedWorkerThreads / maxWorkerThreads;
        var pendingWorkItems = ThreadPool.PendingWorkItemCount;

        var data = new Dictionary<string, object>
        {
            ["availableWorkerThreads"] = availableWorkerThreads,
            ["maxWorkerThreads"] = maxWorkerThreads,
            ["minWorkerThreads"] = minWorkerThreads,
            ["availableIoThreads"] = availableIoThreads,
            ["maxIoThreads"] = maxIoThreads,
            ["minIoThreads"] = minIoThreads,
            ["workerUtilization"] = workerUtilization,
            ["pendingWorkItems"] = pendingWorkItems,
            ["threadCount"] = ThreadPool.ThreadCount
        };

        if (workerUtilization >= 0.95d || pendingWorkItems >= 2000)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Thread pool pressure is critically high.", data: data));
        }

        if (workerUtilization >= 0.85d || pendingWorkItems >= 500)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Thread pool pressure is elevated.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Thread pool pressure is healthy.", data));
    }
}