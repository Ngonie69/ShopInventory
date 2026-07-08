using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that fetches daily stock snapshots from SAP for each monitored warehouse.
/// Enablement (DailyStock:EnableAutoStockFetch) and the run time (DailyStock:StockFetchTimeCAT)
/// are applied via the cron trigger in QuartzConfiguration; the job performs one fetch per fire.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DailyStockSnapshotJob : IJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyStockSnapshotJob> _logger;
    private readonly DailyStockSettings _settings;

    public DailyStockSnapshotJob(
        IServiceProvider serviceProvider,
        IOptions<DailyStockSettings> settings,
        ILogger<DailyStockSnapshotJob> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        await RunStockFetchAsync(context.CancellationToken);
    }

    private async Task RunStockFetchAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting daily stock snapshot fetch for {Count} warehouses",
            _settings.MonitoredWarehouses.Count);

        using var scope = _serviceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<FetchDailyStockHandler>();

        var today = DateTime.UtcNow.Date;
        var failedWarehouses = new List<string>();

        foreach (var warehouse in _settings.MonitoredWarehouses)
        {
            try
            {
                await handler.FetchWarehouseStockAsync(today, warehouse, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch stock for warehouse {Warehouse}", warehouse);
                failedWarehouses.Add(warehouse);
            }
        }

        // Retry failed warehouses once
        foreach (var warehouse in failedWarehouses)
        {
            try
            {
                _logger.LogInformation("Retrying stock fetch for warehouse {Warehouse}", warehouse);
                await handler.FetchWarehouseStockAsync(today, warehouse, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retry failed for warehouse {Warehouse}", warehouse);
            }
        }

        _logger.LogInformation("Daily stock snapshot fetch complete");
    }
}
