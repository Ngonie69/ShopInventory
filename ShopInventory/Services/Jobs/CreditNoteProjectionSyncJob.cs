using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Cluster-safe SAP credit-note projection job. The sync service owns resumable backfill,
/// overlapping incremental reads, and daily reconciliation.
/// </summary>
[DisallowConcurrentExecution]
public sealed class CreditNoteProjectionSyncJob(
    IServiceScopeFactory scopeFactory,
    ILogger<CreditNoteProjectionSyncJob> logger
) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<ICreditNoteProjectionSyncService>();

        try
        {
            await syncService.SyncAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled credit-note projection synchronization failed");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
