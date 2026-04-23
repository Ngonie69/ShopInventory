using Microsoft.Extensions.DependencyInjection;

namespace ShopInventory.Services;

public sealed class MobileOrderPostProcessingBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<MobileOrderPostProcessingBackgroundService> logger
) : BackgroundService
{
    private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _staleProcessingThreshold = TimeSpan.FromMinutes(1);
    private readonly int _batchSize = 10;
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Mobile order post-processing background service started");

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error while processing durable mobile order post-save queue");
            }

            var jitter = Random.Shared.Next(-1000, 1000);
            await Task.Delay(_processingInterval + TimeSpan.FromMilliseconds(jitter), stoppingToken);
        }

        logger.LogInformation("Mobile order post-processing background service stopped");
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        if (!await _processingSemaphore.WaitAsync(0, stoppingToken))
        {
            logger.LogDebug("Mobile order post-processing is already running, skipping");
            return;
        }

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IMobileOrderPostProcessingQueue>();
            var salesOrderService = scope.ServiceProvider.GetRequiredService<ISalesOrderService>();

            await queue.ResetStaleProcessingEntriesAsync(_staleProcessingThreshold, stoppingToken);

            var entries = await queue.GetNextBatchForProcessingAsync(_batchSize, stoppingToken);
            if (!entries.Any())
            {
                logger.LogDebug("No pending mobile order post-processing queue entries");
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