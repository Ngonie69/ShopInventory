using Quartz;
using ShopInventory.Services.Telematics;

namespace ShopInventory.Services;

/// <summary>
/// Refreshes the cached telematics fleet once a night, and once shortly after a start.
/// </summary>
/// <remarks>
/// The startup run is what makes the registration picker on <c>/van-sales/routes</c> work on a
/// fresh deployment: without it an administrator would open the page the morning after a release
/// and find an empty vehicle list with nothing to say why.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class CartrackFleetSyncJob(
    IServiceScopeFactory scopeFactory,
    ILogger<CartrackFleetSyncJob> logger) : IJob
{
    public const string JobName = "cartrack-fleet-sync";

    public const string StartupTriggerName = "cartrack-fleet-sync-startup";

    /// <summary>
    /// Long enough to be behind the API's own start-up work. This is one request against an
    /// external service and nothing waits on it, so there is no reason for it to compete.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = scopeFactory.CreateScope();

        var sync = scope.ServiceProvider.GetRequiredService<ICartrackFleetSyncService>();

        try
        {
            await sync.SyncAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CartrackRateLimitedException ex)
        {
            // Not a failure worth a stack trace: the account is busy and tomorrow's run will do
            // the same work. Refiring immediately would add to the violation.
            logger.LogWarning(
                "Cartrack fleet sync deferred — the account is rate limited for {Seconds}s.",
                ex.RetryAfter.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled Cartrack fleet sync failed");

            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
