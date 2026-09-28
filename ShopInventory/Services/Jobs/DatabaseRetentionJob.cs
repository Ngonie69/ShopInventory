using Microsoft.EntityFrameworkCore;
using Quartz;
using ShopInventory.Data;

namespace ShopInventory.Services;

/// <summary>
/// Nightly deletion of rows nothing needs any more, table by table, in batches.
/// </summary>
/// <remarks>
/// <para>
/// Each rule deletes only rows the application already treats as gone or finished: a notification
/// no user can see, an idempotency claim the store itself would discard, a refresh token long
/// expired. Each keeps a margin past that point, so a check that still looks such rows up (a
/// notification de-dupe, a replayed-token check) goes on finding them for as long as it matters.
/// </para>
/// <para>
/// Deliberately not here, so do not add them without reading why:
/// <list type="bullet">
/// <item><c>AuditLogs</c> and <c>WhatsAppWebhookEvents</c> are kept for good. The audit trail is
/// unconditional, and the WhatsApp events are the inbox people search.</item>
/// <item><c>InvoiceQueue</c> and <c>InventoryTransferQueue</c>: a finished row is what refuses a
/// resent external reference, and on the invoice side it is also what stops a second fiscal
/// receipt for a van-sale invoice. Deleting one would reopen the duplicate it guards.</item>
/// <item>The other queue tables are indexed on (Status, Priority, CreatedAt), so finished rows
/// cost their readers nothing, and their status-by-reference lookups keep answering.</item>
/// </list>
/// </para>
/// <para>
/// Batched because the first run meets every row that ever expired, and one statement deleting
/// all of that holds its locks for as long as it takes.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class DatabaseRetentionJob(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseRetentionJob> logger) : IJob
{
    public const string JobName = "database-retention";

    private const int BatchSize = 5000;

    /// <summary>What each table keeps, and so what the job may delete.</summary>
    public static readonly IReadOnlyList<RetentionRule> Rules =
    [
        // Invisible to every user from ExpiresAt (30 days after creation). The margin is for the
        // de-dupe checks that look notifications up with no date limit: an approval still pending
        // after it is deleted is notified once more, at most every four months.
        Rule("Notifications", (db, now) => db.Notifications
            .Where(row => row.ExpiresAt != null && row.ExpiresAt < now.AddDays(-90))
            .OrderBy(row => row.Id)),

        // Full response bodies; read only by the admin delivery history.
        Rule("WebhookDeliveries", (db, now) => db.WebhookDeliveries
            .Where(row => row.CreatedAt < now.AddDays(-90))
            .OrderBy(row => row.Id)),

        // Sent, or failed for the last time (the queue retries a failure until AttemptCount is 3).
        Rule("EmailQueueItems", (db, now) => db.EmailQueueItems
            .Where(row =>
                (row.Status == "Sent" && row.CreatedAt < now.AddDays(-30)) ||
                (row.Status == "Failed" && row.AttemptCount >= 3 && row.CreatedAt < now.AddDays(-90)))
            .OrderBy(row => row.Id)),

        // Staff tokens last 7 days, and the rotation grace needs a revoked row, not an expired one.
        Rule("RefreshTokens", (db, now) => db.RefreshTokens
            .Where(row => row.ExpiresAt < now.AddDays(-30))
            .OrderBy(row => row.Id)),

        // A replayed revoked token revokes its device's whole chain, and that check needs the row.
        // Kept 90 days past expiry; a token older than that is refused as unknown instead.
        Rule("VanSalesCustomerRefreshTokens", (db, now) => db.VanSalesCustomerRefreshTokens
            .Where(row => row.ExpiresAt < now.AddDays(-90))
            .OrderBy(row => row.Id)),

        // The store already treats an expired claim as absent and deletes it when next asked. A
        // live lease has a future expiry, so it is never touched.
        Rule("IdempotencyRequests", (db, now) => db.IdempotencyRequests
            .Where(row => row.ExpiresAtUtc < now.AddDays(-1))
            .OrderBy(row => row.Id)),

        Rule("OfflineQueueItems", (db, now) => db.OfflineQueueItems
            .Where(row => row.Status == "Completed" && row.CompletedAt < now.AddDays(-30))
            .OrderBy(row => row.Id)),

        // Blocked clients are kept whatever their age: unblocking is an admin's call.
        Rule("ApiRateLimits", (db, now) => db.ApiRateLimits
            .Where(row => !row.IsBlocked && row.LastRequestAt < now.AddDays(-30))
            .OrderBy(row => row.Id)),

        // Revoked devices only. A live registration can go months without a push, so age alone
        // says nothing; and registering a deleted token again simply adds it back.
        Rule("PushDeviceRegistrations", (db, now) => db.PushDeviceRegistrations
            .Where(row => row.IsRevoked && (row.LastActiveAt ?? row.RegisteredAt) < now.AddDays(-90))
            .OrderBy(row => row.Id)),

        Rule("VanSalesCustomerDevices", (db, now) => db.VanSalesCustomerDevices
            .Where(row => row.IsRevoked && (row.LastActiveAt ?? row.RegisteredAt) < now.AddDays(-90))
            .OrderBy(row => row.Id)),

        Rule("PasswordResetTokens", (db, now) => db.PasswordResetTokens
            .Where(row => row.ExpiresAt < now.AddDays(-30))
            .OrderBy(row => row.Id)),

        // The resend throttle only looks at the last few minutes.
        Rule("VanSalesCustomerOtps", (db, now) => db.VanSalesCustomerOtps
            .Where(row => row.ExpiresAt < now.AddDays(-7))
            .OrderBy(row => row.Id)),
    ];

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var removed = await PurgeAsync(db, DateTime.UtcNow, BatchSize, context.CancellationToken);

            foreach (var (table, count) in removed.Where(entry => entry.Count > 0))
            {
                logger.LogInformation("Retention removed {Count} row(s) from {Table}", count, table);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database retention failed");

            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }

    /// <summary>Applies every rule, a batch at a time, and returns how many rows each removed.</summary>
    public static async Task<IReadOnlyList<(string Table, int Count)>> PurgeAsync(
        ApplicationDbContext db, DateTime nowUtc, int batchSize, CancellationToken cancellationToken)
    {
        var removed = new List<(string Table, int Count)>();

        foreach (var rule in Rules)
        {
            var total = 0;
            int batch;
            do
            {
                batch = await rule.DeleteBatchAsync(db, nowUtc, batchSize, cancellationToken);
                total += batch;
            }
            while (batch == batchSize);

            removed.Add((rule.Table, total));
        }

        return removed;
    }

    private static RetentionRule Rule<TEntity>(
        string table,
        Func<ApplicationDbContext, DateTime, IQueryable<TEntity>> expired)
        where TEntity : class =>
        new(table, (db, now, batchSize, cancellationToken) =>
            expired(db, now).Take(batchSize).ExecuteDeleteAsync(cancellationToken));

    /// <summary>One table's rule: delete up to a batch of its expired rows, oldest key first.</summary>
    public sealed record RetentionRule(
        string Table,
        Func<ApplicationDbContext, DateTime, int, CancellationToken, Task<int>> DeleteBatchAsync);
}
