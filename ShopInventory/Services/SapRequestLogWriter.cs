using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Drains <see cref="SapRequestLogQueue"/> into <c>SapConnectionLogs</c>, off the SAP request path.
/// </summary>
/// <remarks>
/// This work used to run inline in <c>SAPRequestLoggingHandler</c>: an insert plus a trim query plus
/// a delete, awaited before the SAP response was returned, and — because the logging handler is the
/// innermost one — while the caller still held one of the six process-wide SAP concurrency slots and
/// a pooled connection to SAP. Postgres latency was therefore throttling SAP throughput. Batching
/// here means a burst of SAP calls costs one round-trip rather than three per call, and none of it
/// is on anybody's critical path.
/// </remarks>
public sealed class SapRequestLogWriter(
    SapRequestLogQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<SapRequestLogWriter> logger) : BackgroundService
{
    /// <summary>How many of the most recent entries the table keeps.</summary>
    internal const int RetainedEntries = 100;

    /// <summary>
    /// Entries written between trims. Trimming is housekeeping — readers only ever ask for the most
    /// recent handful (see <c>SyncStatusService</c>) — so paying for it once per this many rows
    /// rather than once per row is the whole point. The table stays bounded at roughly
    /// <see cref="RetainedEntries"/> + this.
    /// </summary>
    internal const int WritesBetweenTrims = 100;

    /// <summary>
    /// How long to keep collecting after the first entry arrives, so a burst of SAP calls lands as
    /// one insert. Short enough that the sync history screen still looks live.
    /// </summary>
    private static readonly TimeSpan BatchLinger = TimeSpan.FromMilliseconds(200);

    private int _writesSinceTrim;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SAP request log writer started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await queue.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                // Let a burst accumulate rather than writing the first entry on its own.
                await Task.Delay(BatchLinger, stoppingToken);

                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Sync history is diagnostic. Never let a database problem stop the writer, or the
                // queue silently fills and drops everything from here on.
                logger.LogWarning(ex, "Failed to persist a batch of SAP request logs");
                await SafeDelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        // Best effort on shutdown so the last few calls of a run are not lost.
        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to flush remaining SAP request logs during shutdown");
        }

        logger.LogInformation("SAP request log writer stopped");
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batch = new List<SapConnectionLog>();
        while (queue.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        context.SapConnectionLogs.AddRange(batch);
        await context.SaveChangesAsync(cancellationToken);

        _writesSinceTrim += batch.Count;
        if (_writesSinceTrim < WritesBetweenTrims)
        {
            return;
        }

        _writesSinceTrim = 0;
        await TrimAsync(context, cancellationToken);
    }

    private static async Task TrimAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        var oldestRetained = await context.SapConnectionLogs
            .OrderByDescending(log => log.CheckedAt)
            .ThenByDescending(log => log.Id)
            .Skip(RetainedEntries - 1)
            .Select(log => new { log.CheckedAt, log.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (oldestRetained is null)
        {
            return;
        }

        // Delete by the retention boundary rather than by a materialised id list: one statement,
        // and it stays correct however many rows accumulated since the last trim.
        await context.SapConnectionLogs
            .Where(log => log.CheckedAt < oldestRetained.CheckedAt
                || (log.CheckedAt == oldestRetained.CheckedAt && log.Id < oldestRetained.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
