using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

/// <summary>
/// Once a day, deletes the customer portal's rows nothing needs any more: security log entries
/// over a year old, refresh tokens long expired, and rate-limit counters whose window closed a
/// week ago.
/// </summary>
/// <remarks>
/// <para>
/// The Web app has no scheduler, so this is a hosted service. Every running slot runs it; the
/// deletes are idempotent, so a second slot finds nothing left to do.
/// </para>
/// <para>
/// The Web's own <c>AuditLogs</c> are kept for good, as the API's are. The API's tables are
/// trimmed by its <c>DatabaseRetentionJob</c>.
/// </para>
/// </remarks>
public sealed class CustomerPortalRetentionService(
    IDbContextFactory<WebAppDbContext> dbContextFactory,
    ILogger<CustomerPortalRetentionService> logger) : BackgroundService
{
    private const int BatchSize = 5000;

    // Clear of startup, which is busy enough already, and of a deploy's warm-up probes.
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var removed = await PurgeAsync(db, DateTime.UtcNow, BatchSize, cancellationToken);

            foreach (var (table, count) in removed.Where(entry => entry.Count > 0))
            {
                logger.LogInformation("Retention removed {Count} row(s) from {Table}", count, table);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tomorrow's pass tries again; nothing here is urgent enough to stop the service.
            logger.LogError(ex, "Customer portal retention failed");
        }
    }

    /// <summary>Applies every rule, a batch at a time, and returns how many rows each removed.</summary>
    public static async Task<IReadOnlyList<(string Table, int Count)>> PurgeAsync(
        WebAppDbContext db, DateTime nowUtc, int batchSize, CancellationToken cancellationToken)
    {
        return
        [
            // What customers see as their own activity history, and admins' last 50.
            ("CustomerSecurityLogs", await DeleteInBatchesAsync(
                db.CustomerSecurityLogs
                    .Where(row => row.Timestamp < nowUtc.AddDays(-365))
                    .OrderBy(row => row.Id),
                batchSize, cancellationToken)),

            // A replayed revoked token revokes every token the customer holds, and that check needs
            // the row. Kept 90 days past expiry; an older token is refused as unknown instead.
            ("CustomerRefreshTokens", await DeleteInBatchesAsync(
                db.CustomerRefreshTokens
                    .Where(row => row.ExpiresAt < nowUtc.AddDays(-90))
                    .OrderBy(row => row.Id),
                batchSize, cancellationToken)),

            // A window lasts minutes and resets when it is next used, so a counter untouched for a
            // week is the same as none. An active block is kept whatever its age.
            ("CustomerRateLimits", await DeleteInBatchesAsync(
                db.CustomerRateLimits
                    .Where(row => row.WindowStart < nowUtc.AddDays(-7) &&
                                  (row.BlockedUntil == null || row.BlockedUntil < nowUtc))
                    .OrderBy(row => row.Id),
                batchSize, cancellationToken)),
        ];
    }

    private static async Task<int> DeleteInBatchesAsync<TEntity>(
        IQueryable<TEntity> expired, int batchSize, CancellationToken cancellationToken)
        where TEntity : class
    {
        var total = 0;
        int batch;
        do
        {
            batch = await expired.Take(batchSize).ExecuteDeleteAsync(cancellationToken);
            total += batch;
        }
        while (batch == batchSize);

        return total;
    }
}
