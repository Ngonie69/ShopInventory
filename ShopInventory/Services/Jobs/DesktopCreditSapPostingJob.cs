using Quartz;
using ShopInventory.Features.DesktopCreditNotes;

namespace ShopInventory.Services;

/// <summary>
/// Raises the SAP credit memos that fiscal credits are still owed.
///
/// The counterpart of <see cref="DesktopSalePostingJob"/> on the reversal side. A credit taken at the
/// counter is with ZIMRA at once and cannot reach SAP until the sale it reverses does, so between
/// those two moments this job is the only thing that owns it — without it, a credit whose sale posted
/// while nobody was watching would leave the two systems disagreeing about a return indefinitely and
/// silently.
///
/// <see cref="DisallowConcurrentExecutionAttribute"/>, like its siblings: two passes over one credit
/// could ask SAP for the same memo at the same moment, both be told it does not exist, and both
/// raise one.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DesktopCreditSapPostingJob(
    IServiceProvider serviceProvider,
    ILogger<DesktopCreditSapPostingJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var sweep = scope.ServiceProvider.GetRequiredService<DesktopCreditSapSweep>();

        try
        {
            var result = await sweep.SettleOutstandingAsync(context.CancellationToken);

            if (result.Failed > 0)
            {
                logger.LogWarning("Credit posting sweep completed with {Failed} failures.", result.Failed);
            }
        }
        catch (Exception ex)
        {
            // The next run is the right retry. Rethrowing hands it to Quartz's misfire policy, which is
            // tuned for jobs that fire far less often than this one.
            logger.LogError(ex, "Desktop credit note SAP posting sweep failed.");
        }
    }
}
