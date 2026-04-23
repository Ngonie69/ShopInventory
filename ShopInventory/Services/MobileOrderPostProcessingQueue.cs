using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public sealed class MobileOrderPostProcessingQueue(
    ApplicationDbContext context,
    ILogger<MobileOrderPostProcessingQueue> logger
) : IMobileOrderPostProcessingQueue
{
    public async Task<List<MobileOrderPostProcessingQueueEntity>> GetNextBatchForProcessingAsync(
        int batchSize = 10,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        return await context.MobileOrderPostProcessingQueue
            .Where(q => q.Status == MobileOrderPostProcessingQueueStatus.Pending ||
                       (q.Status == MobileOrderPostProcessingQueueStatus.Failed &&
                        q.RetryCount < q.MaxRetries &&
                        (q.NextRetryAt == null || q.NextRetryAt <= now)))
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkAsProcessingAsync(int queueId, CancellationToken cancellationToken = default)
    {
        var entry = await context.MobileOrderPostProcessingQueue.FindAsync(new object[] { queueId }, cancellationToken);
        if (entry == null)
        {
            return;
        }

        entry.Status = MobileOrderPostProcessingQueueStatus.Processing;
        entry.ProcessingStartedAt = DateTime.UtcNow;
        entry.ProcessedAt = null;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetStaleProcessingEntriesAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - staleAfter;
        var staleEntries = await context.MobileOrderPostProcessingQueue
            .Where(q => q.Status == MobileOrderPostProcessingQueueStatus.Processing &&
                        q.ProcessingStartedAt != null &&
                        q.ProcessingStartedAt <= cutoff)
            .ToListAsync(cancellationToken);

        if (staleEntries.Count == 0)
        {
            return;
        }

        foreach (var entry in staleEntries)
        {
            entry.RetryCount++;
            entry.LastError = TruncateError("Processing was interrupted or timed out before completion.");
            entry.ProcessingStartedAt = null;

            if (entry.RetryCount >= entry.MaxRetries)
            {
                entry.Status = MobileOrderPostProcessingQueueStatus.RequiresReview;
                entry.ProcessedAt = DateTime.UtcNow;
                entry.NextRetryAt = null;
            }
            else
            {
                entry.Status = MobileOrderPostProcessingQueueStatus.Failed;
                entry.ProcessedAt = null;
                entry.NextRetryAt = DateTime.UtcNow;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Reset {Count} stale mobile order post-processing queue entries",
            staleEntries.Count);
    }

    private static string TruncateError(string error)
        => error.Length > 2000 ? error[..2000] : error;
}