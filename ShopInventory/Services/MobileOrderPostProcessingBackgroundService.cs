using Microsoft.Extensions.DependencyInjection;

namespace ShopInventory.Services;

public sealed class MobileOrderPostProcessingBackgroundService : BackgroundService
{
    private const string WorkerName = "mobile-order-post-processing";
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly BackgroundWorkerLeaderElector _leaderElector;
    private readonly BackgroundWorkerHealthRegistry _healthRegistry;
    private readonly ILogger<MobileOrderPostProcessingBackgroundService> _logger;
    private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leadershipRetryInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _staleProcessingThreshold = TimeSpan.FromMinutes(1);
    private readonly int _batchSize = 10;
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1);

    public MobileOrderPostProcessingBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        BackgroundWorkerLeaderElector leaderElector,
        BackgroundWorkerHealthRegistry healthRegistry,
        ILogger<MobileOrderPostProcessingBackgroundService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _leaderElector = leaderElector;
        _healthRegistry = healthRegistry;
        _logger = logger;
        _healthRegistry.RegisterWorker(WorkerName, critical: true, healthyWindow: TimeSpan.FromMinutes(2));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Mobile order post-processing background service started");

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
            _logger.LogInformation("Mobile order post-processing leadership acquired on this instance");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessQueueAsync(stoppingToken);
                        _healthRegistry.MarkSuccessfulRun(WorkerName);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _healthRegistry.MarkFailure(WorkerName, ex);
                        _logger.LogError(ex,
                            "Error while processing durable mobile order post-save queue");
                    }

                    var jitter = Random.Shared.Next(-1000, 1000);
                    await Task.Delay(_processingInterval + TimeSpan.FromMilliseconds(jitter), stoppingToken);
                }
            }
            finally
            {
                _healthRegistry.MarkStandby(WorkerName);
            }
        }

        _healthRegistry.MarkStopped(WorkerName);
        _logger.LogInformation("Mobile order post-processing background service stopped");
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        if (!await _processingSemaphore.WaitAsync(0, stoppingToken))
        {
            _logger.LogDebug("Mobile order post-processing is already running, skipping");
            return;
        }

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IMobileOrderPostProcessingQueue>();
            var salesOrderService = scope.ServiceProvider.GetRequiredService<ISalesOrderService>();

            await queue.ResetStaleProcessingEntriesAsync(_staleProcessingThreshold, stoppingToken);

            var entries = await queue.GetNextBatchForProcessingAsync(_batchSize, stoppingToken);
            if (!entries.Any())
            {
                _logger.LogDebug("No pending mobile order post-processing queue entries");
                return;
            }

            foreach (var entry in entries)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                await queue.MarkAsProcessingAsync(entry.Id, stoppingToken);
                await salesOrderService.ProcessMobileOrderPostSaveAsync(entry.SalesOrderId, stoppingToken);
            }
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }
}