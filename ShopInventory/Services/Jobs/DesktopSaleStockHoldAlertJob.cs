using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Hourly: tells the admins about till sales stuck before SAP whose units are held off the shelf, and
/// about the holds that are ending or have ended. See <see cref="DesktopSaleStockHoldAlerter"/>.
/// </summary>
/// <remarks>
/// Hourly because the holds end at the morning fetch and a stuck sale should be in front of someone the
/// same morning. A pass that finds nothing is one indexed query.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class DesktopSaleStockHoldAlertJob(
    IServiceProvider serviceProvider,
    ILogger<DesktopSaleStockHoldAlertJob> logger) : IJob
{
    public const string JobName = "desktop-sale-stock-hold-alert";

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var alerter = scope.ServiceProvider.GetRequiredService<DesktopSaleStockHoldAlerter>();

        try
        {
            await alerter.AlertAsync(DateTime.UtcNow, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing is stamped until its notification is stored, so the next pass sends what this
            // one could not.
            logger.LogError(ex, "Till sale stock-hold alert pass failed.");
        }
    }
}
