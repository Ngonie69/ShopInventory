namespace ShopInventory.Services;

/// <summary>
/// Registers this node's build before the scheduler starts, then keeps the registration fresh.
/// </summary>
/// <remarks>
/// The first refresh is awaited in <see cref="StartAsync"/>, and this service is registered ahead of
/// Quartz's own hosted service, so a node that boots into a cluster already running newer code knows it
/// before the first trigger can fire rather than one heartbeat later.
/// </remarks>
public sealed class ClusterNodeHeartbeatService(
    ClusterBuildRegistry registry,
    ILogger<ClusterNodeHeartbeatService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await registry.RefreshAsync(cancellationToken);

        logger.LogInformation(
            "Cluster node {NodeKey} is running the build {Build}.",
            registry.NodeKey, registry.BuildDescription);

        _loop = RunAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }

        await registry.DeregisterAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await registry.RefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _stopping.Dispose();
}
