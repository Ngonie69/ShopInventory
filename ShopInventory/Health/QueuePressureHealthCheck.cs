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
            if (snapshot.ActiveCount >= CriticalCount || snapshot.OldestActiveAge >= CriticalAge)
            {
                healthStatus = HealthStatus.Unhealthy;
                issues.Add($"{snapshot.Name} retry backlog is critical ({snapshot.ActiveCount} items, oldest {snapshot.OldestActiveAge.TotalMinutes:N0}m).");
                continue;
            }

            if (snapshot.ActiveCount >= WarningCount || snapshot.OldestActiveAge >= WarningAge)
            {
                if (healthStatus == HealthStatus.Healthy)
                {
                    healthStatus = HealthStatus.Degraded;
                }

                issues.Add($"{snapshot.Name} retry backlog is elevated ({snapshot.ActiveCount} items, oldest {snapshot.OldestActiveAge.TotalMinutes:N0}m).");
            }

            // Depth only, deliberately not age. The manual review queue drains at the rate staff
            // work through it, so an age threshold reports "nobody has clicked yet" — which is the
            // normal state overnight and at weekends — as a system health failure, and pins the
            // whole service to Degraded until someone comes in. A deep queue is a real signal; an
            // old one is not.
            if (snapshot.ManualReviewCount >= WarningCount)
            {
                if (healthStatus == HealthStatus.Healthy)
                {
                    healthStatus = HealthStatus.Degraded;
                }

                issues.Add($"{snapshot.Name} manual review queue is elevated ({snapshot.ManualReviewCount} items, oldest {snapshot.OldestManualReviewAge.TotalMinutes:N0}m).");
            }
        }

        var data = new Dictionary<string, object>
        {
            ["queues"] = snapshots.Select(snapshot =>
                $"{snapshot.Name}|activeCount={snapshot.ActiveCount}|oldestActiveMinutes={snapshot.OldestActiveAge.TotalMinutes:N0}|manualReviewCount={snapshot.ManualReviewCount}|oldestManualReviewMinutes={snapshot.OldestManualReviewAge.TotalMinutes:N0}")
                .ToArray()
        };

        if (issues.Count > 0)
        {
            data["issues"] = issues.ToArray();
        }

        // The per-queue detail goes in the description, not just in data: the framework's health
        // check logger and the alert email both render Description and neither reads Data, so a
        // summary-only description left operators with "Queue backlog is elevated" and no way to
        // tell which queue, how deep, or how old without querying the database by hand.
        return healthStatus switch
        {
            HealthStatus.Unhealthy => HealthCheckResult.Unhealthy(
                Describe("Queue backlog is critically high.", issues),
                data: data),
            HealthStatus.Degraded => HealthCheckResult.Degraded(
                Describe("Queue backlog is elevated.", issues),
                data: data),
            _ => HealthCheckResult.Healthy("Queue backlog is healthy.", data)
        };
    }

    private static string Describe(string summary, List<string> issues)
        => issues.Count > 0 ? $"{summary} {string.Join(" ", issues)}" : summary;

    private static async Task<QueueSnapshot> GetInvoiceQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var activeStatuses = new[]
        {
            InvoiceQueueStatus.Pending,
            InvoiceQueueStatus.Processing,
            InvoiceQueueStatus.Failed
        };
        var manualReviewStatuses = new[] { InvoiceQueueStatus.RequiresReview };

        var activeCount = await dbContext.InvoiceQueue
            .AsNoTracking()
            .CountAsync(queue => activeStatuses.Contains(queue.Status), cancellationToken);

        var oldestActiveAt = await dbContext.InvoiceQueue
            .AsNoTracking()
            .Where(queue => activeStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var manualReviewCount = await dbContext.InvoiceQueue
            .AsNoTracking()
            .CountAsync(queue => manualReviewStatuses.Contains(queue.Status), cancellationToken);

        var oldestManualReviewAt = await dbContext.InvoiceQueue
            .AsNoTracking()
            .Where(queue => manualReviewStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("invoice-queue", activeCount, oldestActiveAt, manualReviewCount, oldestManualReviewAt, utcNow);
    }

    private static async Task<QueueSnapshot> GetInventoryTransferQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var activeStatuses = new[]
        {
            InventoryTransferQueueStatus.Pending,
            InventoryTransferQueueStatus.Processing,
            InventoryTransferQueueStatus.Failed
        };
        var manualReviewStatuses = new[] { InventoryTransferQueueStatus.RequiresReview };

        var activeCount = await dbContext.InventoryTransferQueue
            .AsNoTracking()
            .CountAsync(queue => activeStatuses.Contains(queue.Status), cancellationToken);

        var oldestActiveAt = await dbContext.InventoryTransferQueue
            .AsNoTracking()
            .Where(queue => activeStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var manualReviewCount = await dbContext.InventoryTransferQueue
            .AsNoTracking()
            .CountAsync(queue => manualReviewStatuses.Contains(queue.Status), cancellationToken);

        var oldestManualReviewAt = await dbContext.InventoryTransferQueue
            .AsNoTracking()
            .Where(queue => manualReviewStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("inventory-transfer-queue", activeCount, oldestActiveAt, manualReviewCount, oldestManualReviewAt, utcNow);
    }

    private static async Task<QueueSnapshot> GetMobileOrderQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var activeStatuses = new[]
        {
            MobileOrderPostProcessingQueueStatus.Pending,
            MobileOrderPostProcessingQueueStatus.Processing,
            MobileOrderPostProcessingQueueStatus.Failed
        };
        var manualReviewStatuses = new[] { MobileOrderPostProcessingQueueStatus.RequiresReview };

        var activeCount = await dbContext.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .CountAsync(queue => activeStatuses.Contains(queue.Status), cancellationToken);

        var oldestActiveAt = await dbContext.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .Where(queue => activeStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var manualReviewCount = await dbContext.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .CountAsync(queue => manualReviewStatuses.Contains(queue.Status), cancellationToken);

        var oldestManualReviewAt = await dbContext.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .Where(queue => manualReviewStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("mobile-order-post-processing", activeCount, oldestActiveAt, manualReviewCount, oldestManualReviewAt, utcNow);
    }

    private static async Task<QueueSnapshot> GetIncomingPaymentQueueSnapshotAsync(ApplicationDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken)
    {
        var activeStatuses = new[]
        {
            IncomingPaymentQueueStatus.Pending,
            IncomingPaymentQueueStatus.Processing,
            IncomingPaymentQueueStatus.Failed
        };
        var manualReviewStatuses = new[] { IncomingPaymentQueueStatus.RequiresReview };

        var activeCount = await dbContext.IncomingPaymentQueue
            .AsNoTracking()
            .CountAsync(queue => activeStatuses.Contains(queue.Status), cancellationToken);

        var oldestActiveAt = await dbContext.IncomingPaymentQueue
            .AsNoTracking()
            .Where(queue => activeStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var manualReviewCount = await dbContext.IncomingPaymentQueue
            .AsNoTracking()
            .CountAsync(queue => manualReviewStatuses.Contains(queue.Status), cancellationToken);

        var oldestManualReviewAt = await dbContext.IncomingPaymentQueue
            .AsNoTracking()
            .Where(queue => manualReviewStatuses.Contains(queue.Status))
            .OrderBy(queue => queue.CreatedAt)
            .Select(queue => (DateTime?)queue.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new QueueSnapshot("incoming-payment-queue", activeCount, oldestActiveAt, manualReviewCount, oldestManualReviewAt, utcNow);
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

        return new QueueSnapshot("offline-queue", pendingCount, oldestPendingAt, 0, null, utcNow);
    }

    private sealed record QueueSnapshot(
        string Name,
        int ActiveCount,
        DateTime? OldestActiveAt,
        int ManualReviewCount,
        DateTime? OldestManualReviewAt,
        DateTime UtcNow)
    {
        public TimeSpan OldestActiveAge => OldestActiveAt.HasValue ? UtcNow - OldestActiveAt.Value : TimeSpan.Zero;
        public TimeSpan OldestManualReviewAge => OldestManualReviewAt.HasValue ? UtcNow - OldestManualReviewAt.Value : TimeSpan.Zero;
    }
}