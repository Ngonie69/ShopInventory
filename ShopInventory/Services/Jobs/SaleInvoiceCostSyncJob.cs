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
    /// <summary>
    /// Every 15 minutes from 20:00 to 05:45 CAT, so never during trading hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It ran every 5 minutes all day until 13 Nov 2026. Its read is tiny (one warehouse's INV1 lines over a
    /// short run of invoice numbers, 33-100 ms when it answers), but the Service Layer sometimes never
    /// answers it and keeps a worker spinning at full CPU, growing to several GB, long after the read gave up.
    /// On 13 Nov, from 00:00 to 12:05, it hung 23 times, one in five of all the API's hung SAP reads that
    /// morning. A hung run's invoices stay uncosted, so each following run asked for them again, and could
    /// hang again: CORMACH2's from DocEntry 2426725 were asked for 7 times between 09:51 and 10:29. Those
    /// spinning workers are what starve HANA of memory on the SAP host.
    /// </para>
    /// <para>
    /// Nobody waits on it — the reports read the stored copy — so the cost of moving it out of the day is
    /// only that a sale shows its margin from the evening it posted rather than minutes after. Every
    /// 15 rather than 5 minutes, because overnight nothing stops a spinning worker, and a run that hangs
    /// is asked again on the next one; a day's invoices still fit in the first run or two. The last run
    /// starts at 05:45 and its four-minute budget ends it before 06:00.
    /// </para>
    /// </remarks>
    public const string Schedule = "0 0/15 20-23,0-5 * * ?";

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
