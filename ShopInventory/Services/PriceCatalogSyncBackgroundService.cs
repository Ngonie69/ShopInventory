using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Features.Prices.Commands.SyncPriceCatalog;

namespace ShopInventory.Services;

public sealed class PriceCatalogSyncBackgroundService : BackgroundService
{
    private const string WorkerName = "price-catalog-sync";
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan LeadershipRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundWorkerLeaderElector _leaderElector;
    private readonly BackgroundWorkerHealthRegistry _healthRegistry;
    private readonly ILogger<PriceCatalogSyncBackgroundService> _logger;

    public PriceCatalogSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        BackgroundWorkerLeaderElector leaderElector,
        BackgroundWorkerHealthRegistry healthRegistry,
        ILogger<PriceCatalogSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _leaderElector = leaderElector;
        _healthRegistry = healthRegistry;
        _logger = logger;

        _healthRegistry.RegisterWorker(WorkerName, critical: true, healthyWindow: TimeSpan.FromHours(5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Price catalog sync background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var leadershipHandle = await _leaderElector.TryAcquireAsync(WorkerName, stoppingToken);
            if (leadershipHandle is null)
            {
                _healthRegistry.MarkStandby(WorkerName);
                await Task.Delay(LeadershipRetryInterval, stoppingToken);
                continue;
            }

            _healthRegistry.MarkLeader(WorkerName);
            _logger.LogInformation("Price catalog sync leadership acquired on this instance");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await RunSyncAsync(stoppingToken);
                        _healthRegistry.MarkSuccessfulRun(WorkerName);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _healthRegistry.MarkFailure(WorkerName, ex);
                        _logger.LogError(ex, "Price catalog sync failed");
                    }

                    await DelayWithHeartbeatAsync(SyncInterval, stoppingToken);
                }
            }
            finally
            {
                _healthRegistry.MarkStandby(WorkerName);
            }
        }

        _healthRegistry.MarkStopped(WorkerName);
        _logger.LogInformation("Price catalog sync background service stopped");
    }

    private async Task RunSyncAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new SyncPriceCatalogCommand(), cancellationToken);
        if (result.IsError)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }

    private async Task DelayWithHeartbeatAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            _healthRegistry.MarkLeader(WorkerName);

            var nextDelay = remaining < HeartbeatInterval ? remaining : HeartbeatInterval;
            await Task.Delay(nextDelay, cancellationToken);
            remaining -= nextDelay;
        }
    }
}