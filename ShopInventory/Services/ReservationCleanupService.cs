using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ShopInventory.Services;

/// <summary>
/// Background service that periodically cleans up expired stock reservations.
/// This ensures that stock held by expired reservations is automatically released.
/// </summary>
public class ReservationCleanupService : BackgroundService
{
    private const string WorkerName = "reservation-cleanup";
    private readonly IServiceProvider _serviceProvider;
    private readonly BackgroundWorkerLeaderElector _leaderElector;
    private readonly BackgroundWorkerHealthRegistry _healthRegistry;
    private readonly ILogger<ReservationCleanupService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _leadershipRetryInterval = TimeSpan.FromSeconds(5);

    public ReservationCleanupService(
        IServiceProvider serviceProvider,
        BackgroundWorkerLeaderElector leaderElector,
        BackgroundWorkerHealthRegistry healthRegistry,
        ILogger<ReservationCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _leaderElector = leaderElector;
        _healthRegistry = healthRegistry;
        _logger = logger;
        _healthRegistry.RegisterWorker(WorkerName, critical: true, healthyWindow: TimeSpan.FromMinutes(3));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reservation cleanup service starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var leadershipHandle = await _leaderElector.TryAcquireAsync(WorkerName, stoppingToken);
            if (leadershipHandle is null)
            {
                _healthRegistry.MarkStandby(WorkerName);
                await Task.Delay(_leadershipRetryInterval, stoppingToken);
                continue;
            }

            _healthRegistry.MarkLeader(WorkerName);
            _logger.LogInformation("Reservation cleanup leadership acquired on this instance");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await CleanupExpiredReservationsAsync(stoppingToken);
                        _healthRegistry.MarkSuccessfulRun(WorkerName);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _healthRegistry.MarkFailure(WorkerName, ex);
                        _logger.LogError(ex, "Error during reservation cleanup");
                    }

                    try
                    {
                        await Task.Delay(_checkInterval, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _healthRegistry.MarkStandby(WorkerName);
            }
        }

        _healthRegistry.MarkStopped(WorkerName);
        _logger.LogInformation("Reservation cleanup service stopped.");
    }

    private async Task CleanupExpiredReservationsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var reservationService = scope.ServiceProvider.GetRequiredService<IStockReservationService>();

        var expiredCount = await reservationService.ExpireReservationsAsync(cancellationToken);

        if (expiredCount > 0)
        {
            _logger.LogInformation("Cleanup completed: {ExpiredCount} reservations expired and stock released", expiredCount);
        }
    }
}

/// <summary>
/// Implementation of IReservedQuantityProvider that wraps IStockReservationService
/// </summary>
public class ReservedQuantityProvider : IReservedQuantityProvider
{
    private readonly IStockReservationService _reservationService;

    public ReservedQuantityProvider(IStockReservationService reservationService)
    {
        _reservationService = reservationService;
    }

    public Task<decimal> GetReservedQuantityAsync(string itemCode, string warehouseCode, CancellationToken cancellationToken = default)
    {
        return _reservationService.GetReservedQuantityAsync(itemCode, warehouseCode, cancellationToken);
    }

    public Task<decimal> GetReservedBatchQuantityAsync(string itemCode, string warehouseCode, string batchNumber, CancellationToken cancellationToken = default)
    {
        return _reservationService.GetReservedBatchQuantityAsync(itemCode, warehouseCode, batchNumber, cancellationToken);
    }
}
