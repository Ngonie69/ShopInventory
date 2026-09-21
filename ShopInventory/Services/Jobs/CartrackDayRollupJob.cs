using Quartz;
using ShopInventory.Services.Telematics;

namespace ShopInventory.Services;

/// <summary>
/// Builds the per-vehicle, per-day telematics rollup the compliance report reads.
/// </summary>
/// <remarks>
/// Two schedules hang off this one job key — a nightly pass and an hourly one through the trading
/// day — because <see cref="DisallowConcurrentExecutionAttribute"/> is enforced per key. On
/// separate keys the two would happily build the same date at the same time, each reading the
/// same window from the provider and racing to upsert the same rows.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class CartrackDayRollupJob(
    IServiceScopeFactory scopeFactory,
    ILogger<CartrackDayRollupJob> logger) : IJob
{
    public const string JobName = "cartrack-day-rollup";

    public const string HourlyTriggerName = "cartrack-day-rollup-hourly";

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();

        var rollup = scope.ServiceProvider.GetRequiredService<ICartrackRollupService>();

        try
        {
            await rollup.SyncAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CartrackRateLimitedException ex)
        {
            // Stop cleanly on the checkpoint rather than refiring into the same wall. The work is
            // resumable by construction, so the next pass picks up exactly where this one left off
            // and nothing is lost by waiting.
            logger.LogWarning(
                "Fleet telematics rollup deferred — the account is rate limited for {Seconds}s.",
                ex.RetryAfter.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled fleet telematics rollup failed");

            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
