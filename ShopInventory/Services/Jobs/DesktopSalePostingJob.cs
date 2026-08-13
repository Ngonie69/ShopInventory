using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Carries shop till and vending sales to SAP, one invoice and one payment each.
///
/// A till cannot wait for SAP at the counter, so it fiscalises, prints and hands over the receipt while
/// the sale is still only in this system. This job is what closes that gap, which is why it runs on a
/// short interval rather than at end of day: the longer it waits, the longer SAP and everything reading
/// stock or revenue from it disagree with what the shop has actually sold. The evening pass is a mop-up
/// for anything that spent the day failing.
///
/// Running repeatedly is safe by construction rather than by scheduling luck. Each sale posts under its
/// own external reference, and <see cref="DesktopSalePostingService"/> asks SAP for that reference
/// before posting, so a later pass adopts what an earlier one created instead of creating a second.
///
/// <see cref="DisallowConcurrentExecutionAttribute"/> is enforced per job key, so the interval trigger
/// and the evening sweep must hang off the same key. Two keys could both ask SAP, both be told the
/// invoice does not exist, and both create it.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DesktopSalePostingJob(
    IServiceProvider serviceProvider,
    ILogger<DesktopSalePostingJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var postingService = scope.ServiceProvider.GetRequiredService<DesktopSalePostingService>();

        try
        {
            var result = await postingService.PostPendingSalesAsync(context.CancellationToken);

            if (result.Failed > 0)
            {
                logger.LogWarning(
                    "Till sale posting completed with {Failed} failures: {Errors}",
                    result.Failed,
                    string.Join(" | ", result.Errors.Take(10)));
            }
        }
        catch (Exception ex)
        {
            // The next run, a minute away, is the right retry. Rethrowing would hand it to Quartz's
            // misfire policy instead, which is tuned for jobs that fire far less often.
            logger.LogError(ex, "Till sale posting failed.");
        }
    }
}
