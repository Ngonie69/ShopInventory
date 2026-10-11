namespace ShopInventory.Services.SlHealth;

/// <summary>Keeps each node's copy of <see cref="SlHealthMonitor"/> current.</summary>
/// <remarks>
/// Unlike the SAP switch and outage refreshers beside it, the first read does not run in
/// <c>StartAsync</c>. Those two decide whether a job may post, so a node must have them before its
/// first pass. SL Health decides nothing of the kind, and a node must never start three seconds late,
/// or not at all, because SL Health was slow to answer.
/// </remarks>
public sealed class SlHealthRefresher(SlHealthMonitor monitor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!monitor.IsConfigured)
        {
            return;
        }

        try
        {
            await monitor.RefreshAsync(stoppingToken);

            using var timer = new PeriodicTimer(SlHealthMonitor.RefreshInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await monitor.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }
}
