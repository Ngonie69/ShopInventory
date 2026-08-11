using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Hands the fiscalisation platform the receipts vans signed while offline.
///
/// Runs on a short interval rather than at end of day, unlike the SAP posting it sits beside. The two are
/// on different clocks on purpose: SAP only needs a settled day, whereas a receipt has to be archived
/// before its fiscal day is closed, and the day can be closed automatically once the taxpayer's maximum
/// hours are up. A receipt still sitting here when that happens is one ZIMRA never receives.
///
/// <see cref="DisallowConcurrentExecutionAttribute"/> because two runs would walk the same device's chain
/// at once and race each other into a spurious chain break.
/// </summary>
[DisallowConcurrentExecution]
public sealed class VanSalesSignedReceiptIngestJob(
    IServiceProvider serviceProvider,
    ILogger<VanSalesSignedReceiptIngestJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var ingestService = scope.ServiceProvider
            .GetRequiredService<VanSalesSignedReceiptIngestService>();

        try
        {
            var result = await ingestService.IngestPendingReceiptsAsync(context.CancellationToken);

            if (result.PlatformEndpointMissing)
            {
                // Deliberately not warned about per receipt: one line naming the cause is more use than a
                // wall of identical failures, and there is exactly one thing to do about it.
                logger.LogError(
                    "No van receipt can be submitted: the fiscalisation platform does not serve the " +
                    "ingest-signed route, so its build is older than this service. Nothing was marked failed " +
                    "and no attempts were spent — deploy the platform and the backlog drains on the next run.");
            }
            else if (result.ChainBroken > 0 || result.Unsignable > 0)
            {
                logger.LogError(
                    "Signed van receipt ingest stopped {Stopped} handset(s): {ChainBroken} chain break(s), " +
                    "{Unsignable} receipt(s) that can never be submitted. Their fiscal days cannot be closed " +
                    "cleanly until someone reconciles them. {Errors}",
                    result.DevicesStopped,
                    result.ChainBroken,
                    result.Unsignable,
                    string.Join(" | ", result.Errors.Take(10)));
            }
            else if (result.Failed > 0)
            {
                logger.LogWarning(
                    "Signed van receipt ingest left {Failed} receipt(s) unsent; the next run retries them. {Errors}",
                    result.Failed,
                    string.Join(" | ", result.Errors.Take(10)));
            }
        }
        catch (Exception ex)
        {
            // The next interval is the right retry: nothing is lost by waiting, and every receipt keeps
            // its place in the queue.
            logger.LogError(ex, "Signed van receipt ingest failed.");
        }
    }
}
