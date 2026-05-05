using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;

namespace ShopInventory.Services;

/// <summary>
/// Background service that fetches daily stock snapshots from SAP at a configured time (default 7 AM CAT).
/// </summary>
public class DailyStockSnapshotService : BackgroundService
{
    private const string WorkerName = "daily-stock-snapshot";
    private static readonly TimeSpan LeadershipRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly BackgroundWorkerLeaderElector _leaderElector;
    private readonly BackgroundWorkerHealthRegistry _healthRegistry;
    private readonly ILogger<DailyStockSnapshotService> _logger;
    private readonly DailyStockSettings _settings;

    // CAT is UTC+2
    private static readonly TimeSpan CatOffset = TimeSpan.FromHours(2);

    public DailyStockSnapshotService(
        IServiceProvider serviceProvider,
        BackgroundWorkerLeaderElector leaderElector,
        BackgroundWorkerHealthRegistry healthRegistry,
        IOptions<DailyStockSettings> settings,
        ILogger<DailyStockSnapshotService> logger)
    {
        _serviceProvider = serviceProvider;
        _leaderElector = leaderElector;
        _healthRegistry = healthRegistry;
        _logger = logger;
        _settings = settings.Value;

        if (_settings.EnableAutoStockFetch)
        {
            _healthRegistry.RegisterWorker(WorkerName, critical: true, healthyWindow: TimeSpan.FromMinutes(2));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.EnableAutoStockFetch)
        {
            _logger.LogInformation("Automatic daily stock fetch is disabled");
            return;
        }

        _logger.LogInformation("Daily Stock Snapshot Service started — scheduled for {Time} CAT",
            _settings.StockFetchTimeCAT);

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
            _logger.LogInformation("Daily stock snapshot leadership acquired on this instance");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var delay = CalculateDelayUntilNextRun(_settings.StockFetchTimeCAT);
                        _logger.LogInformation("Next stock fetch in {Delay}", delay);
                        await DelayWithHeartbeatAsync(delay, stoppingToken);

                        await RunStockFetchAsync(stoppingToken);
                        _healthRegistry.MarkSuccessfulRun(WorkerName);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _healthRegistry.MarkFailure(WorkerName, ex);
                        _logger.LogError(ex, "Error in daily stock snapshot service");
                        await DelayWithHeartbeatAsync(TimeSpan.FromMinutes(5), stoppingToken);
                    }
                }
            }
            finally
            {
                _healthRegistry.MarkStandby(WorkerName);
            }
        }

        if (_settings.EnableAutoStockFetch)
        {
            _healthRegistry.MarkStopped(WorkerName);
        }
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

    private TimeSpan CalculateDelayUntilNextRun(string timeCAT)
    {
        if (!TimeSpan.TryParse(timeCAT, out var targetTime))
            targetTime = new TimeSpan(7, 0, 0); // Default 07:00

        // Convert current UTC to CAT
        var nowCat = DateTimeOffset.UtcNow.ToOffset(CatOffset);
        var todayTargetCat = nowCat.Date + targetTime;

        var nextRun = nowCat.DateTime < todayTargetCat
            ? todayTargetCat
            : todayTargetCat.AddDays(1);

        return nextRun - nowCat.DateTime;
    }
}
