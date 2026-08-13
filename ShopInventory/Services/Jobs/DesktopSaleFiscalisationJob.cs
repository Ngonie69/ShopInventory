using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises vending sales, which are stored unsigned because nothing is printed and nobody is
/// waiting at a counter.
///
/// Runs ahead of <see cref="DesktopSalePostingJob"/> in the same sense that fiscalisation comes before
/// SAP everywhere else in this system: the posting service only takes sales that have already
/// fiscalised, so this is what feeds it. A vending sale that never reaches this job never reaches SAP
/// either.
///
/// <see cref="DisallowConcurrentExecutionAttribute"/> matters more here than for most jobs. Two passes
/// over the same sale would submit one receipt to FDMS twice, under the same reference — the platform
/// deduplicates on that, but only if the two submissions do not race, and a duplicate fiscal receipt
/// cannot be withdrawn.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DesktopSaleFiscalisationJob(
    IServiceProvider serviceProvider,
    ILogger<DesktopSaleFiscalisationJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var sweep = scope.ServiceProvider.GetRequiredService<DesktopSaleFiscalisationSweep>();

        try
        {
            var result = await sweep.FiscalisePendingSalesAsync(context.CancellationToken);

            if (result.Failed > 0)
            {
                logger.LogWarning(
                    "Fiscalisation sweep completed with {Failed} failures.", result.Failed);
            }
        }
        catch (Exception ex)
        {
            // The next run is the right retry. Rethrowing hands it to Quartz's misfire policy, which
            // is tuned for jobs that fire far less often than this one.
            logger.LogError(ex, "Desktop sale fiscalisation sweep failed.");
        }
    }
}
