using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that drains the durable mobile-order post-processing queue. Runs on an interval
/// trigger; Quartz owns cadence, clustering and misfire handling (see QuartzConfiguration).
/// </summary>
[DisallowConcurrentExecution]
public sealed class MobileOrderPostProcessingJob : IJob
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<MobileOrderPostProcessingJob> _logger;
    private readonly TimeSpan _staleProcessingThreshold = TimeSpan.FromMinutes(1);
    private readonly int _batchSize = 10;

    public MobileOrderPostProcessingJob(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<MobileOrderPostProcessingJob> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        await ProcessQueueAsync(context.CancellationToken);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
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
}
