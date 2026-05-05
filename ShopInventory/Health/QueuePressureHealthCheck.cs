using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Health;

public sealed class QueuePressureHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    private static readonly TimeSpan WarningAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CriticalAge = TimeSpan.FromHours(1);
    private const int WarningCount = 25;
    private const int CriticalCount = 100;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var utcNow = DateTime.UtcNow;

        var snapshots = new List<QueueSnapshot>
        {
            await GetInvoiceQueueSnapshotAsync(dbContext, utcNow, cancellationToken),
            await GetInventoryTransferQueueSnapshotAsync(dbContext, utcNow, cancellationToken),
            await GetIncomingPaymentQueueSnapshotAsync(dbContext, utcNow, cancellationToken),
            await GetMobileOrderQueueSnapshotAsync(dbContext, utcNow, cancellationToken),
            await GetOfflineQueueSnapshotAsync(dbContext, utcNow, cancellationToken)
        };

        var issues = new List<string>();
        var healthStatus = HealthStatus.Healthy;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.PendingCount >= CriticalCount || snapshot.OldestPendingAge >= CriticalAge)
            {
                healthStatus = HealthStatus.Unhealthy;
                issues.Add($"{snapshot.Name} backlog is critical ({snapshot.PendingCount} items, oldest {snapshot.OldestPendingAge.TotalMinutes:N0}m).");
                continue;
            }

            if (snapshot.PendingCount >= WarningCount || snapshot.OldestPendingAge >= WarningAge)
            {
                if (healthStatus == HealthStatus.Healthy)
                {
                    healthStatus = HealthStatus.Degraded;
                }

                issues.Add($"{snapshot.Name} backlog is elevated ({snapshot.PendingCount} items, oldest {snapshot.OldestPendingAge.TotalMinutes:N0}m).");
            }
        }

        var data = new Dictionary<string, object>
        {
            ["queues"] = snapshots.Select(snapshot =>
                $"{snapshot.Name}|count={snapshot.PendingCount}|oldestMinutes={snapshot.OldestPendingAge.TotalMinutes:N0}")
                .ToArray()
        };

        if (issues.Count > 0)
        {
            data["issues"] = issues.ToArray();
        }

        return healthStatus switch
        {
            HealthStatus.Unhealthy => HealthCheckResult.Unhealthy("Queue backlog is critically high.", data: data),
            HealthStatus.Degraded => HealthCheckResult.Degraded("Queue backlog is elevated.", data: data),
            _ => HealthCheckResult.Healthy("Queue backlog is healthy.", data)
        };
    }

    private static async Task<QueueSnapshot> GetInvoiceQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var pendingStatuses = new[]
        {
            InvoiceQueueStatus.Pending,
            InvoiceQueueStatus.Processing,
            InvoiceQueueStatus.Failed,
            InvoiceQueueStatus.RequiresReview
        };

        var pendingCount = await dbContext.InvoiceQueue
            .AsNoTracking()
            .CountAsync(queue => pendingStatuses.Contains(queue.Status), cancellationToken);

        var oldestPendingAt = await dbContext.InvoiceQueue
            .AsNoTracking()
            .Where(queue => pendingStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("invoice-queue", pendingCount, oldestPendingAt, utcNow);
    }

    private static async Task<QueueSnapshot> GetInventoryTransferQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var pendingStatuses = new[]
        {
            InventoryTransferQueueStatus.Pending,
            InventoryTransferQueueStatus.Processing,
            InventoryTransferQueueStatus.Failed,
            InventoryTransferQueueStatus.RequiresReview
        };

        var pendingCount = await dbContext.InventoryTransferQueue
            .AsNoTracking()
            .CountAsync(queue => pendingStatuses.Contains(queue.Status), cancellationToken);

        var oldestPendingAt = await dbContext.InventoryTransferQueue
            .AsNoTracking()
            .Where(queue => pendingStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("inventory-transfer-queue", pendingCount, oldestPendingAt, utcNow);
    }

    private static async Task<QueueSnapshot> GetMobileOrderQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var pendingStatuses = new[]
        {
            MobileOrderPostProcessingQueueStatus.Pending,
            MobileOrderPostProcessingQueueStatus.Processing,
            MobileOrderPostProcessingQueueStatus.Failed,
            MobileOrderPostProcessingQueueStatus.RequiresReview
        };

        var pendingCount = await dbContext.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .CountAsync(queue => pendingStatuses.Contains(queue.Status), cancellationToken);

        var oldestPendingAt = await dbContext.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .Where(queue => pendingStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("mobile-order-post-processing", pendingCount, oldestPendingAt, utcNow);
    }

    private static async Task<QueueSnapshot> GetIncomingPaymentQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var pendingStatuses = new[]
        {
            IncomingPaymentQueueStatus.Pending,
            IncomingPaymentQueueStatus.Processing,
            IncomingPaymentQueueStatus.Failed,
            IncomingPaymentQueueStatus.RequiresReview
        };

        var pendingCount = await dbContext.IncomingPaymentQueue
            .AsNoTracking()
            .CountAsync(queue => pendingStatuses.Contains(queue.Status), cancellationToken);

        var oldestPendingAt = await dbContext.IncomingPaymentQueue
            .AsNoTracking()
            .Where(queue => pendingStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("incoming-payment-queue", pendingCount, oldestPendingAt, utcNow);
    }

    private static async Task<QueueSnapshot> GetOfflineQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var pendingStatuses = new[] { "Pending", "Processing", "Failed" };

        var pendingCount = await dbContext.OfflineQueueItems
            .AsNoTracking()
            .CountAsync(queue => pendingStatuses.Contains(queue.Status), cancellationToken);

        var oldestPendingAt = await dbContext.OfflineQueueItems
            .AsNoTracking()
            .Where(queue => pendingStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("offline-queue", pendingCount, oldestPendingAt, utcNow);
    }

    private sealed record QueueSnapshot(string Name, int PendingCount, DateTime? OldestPendingAt, DateTime UtcNow)
    {
        public TimeSpan OldestPendingAge => OldestPendingAt.HasValue ? UtcNow - OldestPendingAt.Value : TimeSpan.Zero;
    }
}