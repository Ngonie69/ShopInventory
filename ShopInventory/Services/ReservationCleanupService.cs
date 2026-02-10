using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ShopInventory.Services;

/// <summary>
/// Background service that periodically cleans up expired stock reservations.
/// This ensures that stock held by expired reservations is automatically released.
/// </summary>
public class ReservationCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReservationCleanupService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public ReservationCleanupService(
        IServiceProvider serviceProvider,
        ILogger<ReservationCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reservation cleanup service starting...");

        // Wait a bit before first run to allow app to fully start
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredReservationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown, don't log as error
                break;
            }
            catch (Exception ex)
            {
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
