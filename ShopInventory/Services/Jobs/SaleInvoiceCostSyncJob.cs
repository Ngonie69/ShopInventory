using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Copies SAP's booked cost for newly posted sales' invoices, for the reports' gross profit. See
/// <see cref="SaleInvoiceCostSyncService"/>.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SaleInvoiceCostSyncJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SaleInvoiceCostSyncJob> logger
) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<SaleInvoiceCostSyncService>();

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
            logger.LogError(ex, "Scheduled copy of invoice cost from SAP failed");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
