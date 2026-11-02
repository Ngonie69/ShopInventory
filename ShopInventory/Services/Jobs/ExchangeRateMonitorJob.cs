using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Every <c>ExchangeRateMonitor:IntervalMinutes</c> (15): reads SAP's ZiG rate for yesterday through
/// the look-ahead window and raises what changed. See <see cref="ExchangeRateMonitor"/>.
/// </summary>
/// <remarks>
/// DisallowConcurrentExecution is per job key across the cluster, so two nodes never run a pass at
/// once. The page's Check now runs the same pass outside Quartz; the unique alert key and the
/// one-current-value-per-day index keep a collision from doubling anything.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class ExchangeRateMonitorJob(
    IServiceProvider serviceProvider,
    ILogger<ExchangeRateMonitorJob> logger) : IJob
{
    public const string JobName = "exchange-rate-monitor";

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var monitor = scope.ServiceProvider.GetRequiredService<ExchangeRateMonitor>();

        try
        {
            await monitor.CheckAsync(DateTime.UtcNow, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Exchange rate monitor pass failed.");
        }
    }
}
