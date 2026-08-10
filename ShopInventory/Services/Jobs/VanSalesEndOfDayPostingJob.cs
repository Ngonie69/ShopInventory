using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Posts the day's van sales to SAP. Registered against <em>two</em> cron triggers — the main run at
/// 18:00 and a mop-up at 19:30 — because a van that was still out of coverage at 18:00 uploads its
/// backlog afterwards, and without a second pass those sales would sit until the next night's run.
///
/// Running the same job twice is safe by construction, not by scheduling luck: each sale posts under its
/// own <c>U_Van_saleorder</c>, and <c>VanSalesEndOfDayPostingService</c> asks SAP for that key before
/// posting, so the second pass adopts what the first already created. This is why the job does not reuse
/// <c>ConsolidateDailySales</c>, whose key is <c>CONSOL-{date}-{cardCode}</c> — identical on both runs,
/// so a mop-up there would be adopted as the 18:00 invoice and the late sales would vanish silently.
///
/// <see cref="DisallowConcurrentExecutionAttribute"/> means an 18:00 run still going at 19:30 simply
/// skips the mop-up rather than posting alongside itself.
/// </summary>
[DisallowConcurrentExecution]
public sealed class VanSalesEndOfDayPostingJob(
    IServiceProvider serviceProvider,
    ILogger<VanSalesEndOfDayPostingJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = serviceProvider.CreateScope();
        var postingService = scope.ServiceProvider.GetRequiredService<VanSalesEndOfDayPostingService>();

        // Both runs are in the same CAT evening, so "today" is the trading day for each of them. A run
        // that slipped past midnight would need the previous day, which is why this is computed from CAT
        // rather than from UTC — at 19:30 CAT, UtcNow is still the same date, but at 01:00 it would not be.
        var tradingDate = DateTime.UtcNow.AddHours(2).Date;

        try
        {
            var result = await postingService.PostPendingSalesAsync(tradingDate, context.CancellationToken);

            if (result.Failed > 0)
            {
                logger.LogWarning(
                    "Van sales posting for {TradingDate:yyyy-MM-dd} completed with {Failed} failures: {Errors}",
                    result.TradingDate,
                    result.Failed,
                    string.Join(" | ", result.Errors.Take(10)));
            }
        }
        catch (Exception ex)
        {
            // Quartz would otherwise retry per its misfire policy; the next scheduled run is the right
            // retry here, and the mop-up is barely an hour away.
            logger.LogError(
                ex, "Van sales end-of-day posting failed for {TradingDate:yyyy-MM-dd}.", tradingDate);
        }
    }
}
