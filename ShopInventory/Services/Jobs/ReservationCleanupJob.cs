using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that periodically releases stock held by expired reservations.
/// Scheduling, clustering and misfire handling are owned by Quartz (see QuartzConfiguration).
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReservationCleanupJob : IJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReservationCleanupJob> _logger;

    public ReservationCleanupJob(
        IServiceProvider serviceProvider,
        ILogger<ReservationCleanupJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        await CleanupExpiredReservationsAsync(context.CancellationToken);
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
