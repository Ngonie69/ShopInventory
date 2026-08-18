using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Posts held van sales to SAP. Runs on a repeating trigger through the trading day, at 18:00 once the
/// vans are back, and again at 19:30 to mop up any that only regained signal afterwards.
///
/// The interval pass is what makes the day visible. Van sales fiscalise on the handset and are held
/// rather than posted on the request, so without it SAP — and everything reading stock or revenue from
/// it — would learn nothing about a day's trading until the evening. The two evening passes remain
/// because a van out of coverage all afternoon still uploads late.
///
/// Running the same job repeatedly is safe by construction, not by scheduling luck: each sale posts under
/// its own <c>U_Van_saleorder</c>, and <c>VanSalesEndOfDayPostingService</c> asks SAP for that key before
/// posting, so a later pass adopts what an earlier one created. This is why the job does not reuse
/// <c>ConsolidateDailySales</c>, whose key is <c>CONSOL-{date}-{cardCode}</c> — identical on every run,
/// so a second pass there would be adopted as the first's invoice and the late sales would vanish
/// silently.
///
/// <see cref="DisallowConcurrentExecutionAttribute"/> keeps the interval trigger and the 18:00 run from
/// overlapping, which is why they share one job key: Quartz enforces it per key, so a schedule that hung
/// off a key of its own would not be covered by it.
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

        // Every trigger fires within the CAT trading day, so "today" is the trading day for all of them.
        // Computed from CAT rather than UTC because a run after 22:00 CAT is already tomorrow in UTC, and
        // would go looking for a day that has not started.
        //
        // Today is the end of the window, not the whole of it. A sale carries the day the handset sold it
        // on, so a van out of coverage overnight uploads sales already dated to yesterday, and no run ever
        // asks for yesterday again. VanSalesPostingSettings.LookbackDays is what stops those being
        // stranded; passing anything other than today here would move the window, not widen it.
        var tradingDate = DateTime.UtcNow.AddHours(2).Date;

        try
        {
            var result = await postingService.PostPendingSalesAsync(tradingDate, context.CancellationToken);

            if (result.Failed > 0)
            {
                logger.LogWarning(
                    "Van sales posting for {WindowStart:yyyy-MM-dd} to {TradingDate:yyyy-MM-dd} completed with {Failed} failures: {Errors}",
                    result.WindowStart,
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
