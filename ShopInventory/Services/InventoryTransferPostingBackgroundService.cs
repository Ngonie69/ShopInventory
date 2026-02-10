using System.Text.Json;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Background service that processes queued inventory transfers and posts them to SAP in batches
/// </summary>
public class InventoryTransferPostingBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InventoryTransferPostingBackgroundService> _logger;
    private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(10);
    private readonly int _batchSize = 5;
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1);
    private int _consecutiveErrors = 0;
    private const int MaxConsecutiveErrors = 10;

    public InventoryTransferPostingBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<InventoryTransferPostingBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Inventory Transfer Posting Background Service started - Processing every {Interval}s, Batch size: {BatchSize}",
            _processingInterval.TotalSeconds, _batchSize);

        // Initial delay to let the application fully start
        await Task.Delay(TimeSpan.FromSeconds(7), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(stoppingToken);
                _consecutiveErrors = 0; // Reset on success
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                _logger.LogError(ex, "Error in inventory transfer posting background service (consecutive errors: {Count})",
                    _consecutiveErrors);

                // If too many consecutive errors, back off more aggressively
                if (_consecutiveErrors >= MaxConsecutiveErrors)
                {
                    _logger.LogWarning("Too many consecutive errors, backing off for 60 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                    _consecutiveErrors = 0;
                }
            }

            await Task.Delay(_processingInterval, stoppingToken);
        }

        _logger.LogInformation("Inventory Transfer Posting Background Service stopped");
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        if (!await _processingSemaphore.WaitAsync(0, stoppingToken))
        {
            _logger.LogDebug("Transfer queue processing already in progress, skipping");
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var queueService = scope.ServiceProvider.GetRequiredService<IInventoryTransferQueueService>();
            var sapService = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();

            // Get next batch of transfers to process
            var pendingTransfers = await queueService.GetNextBatchForProcessingAsync(_batchSize, stoppingToken);

            if (!pendingTransfers.Any())
            {
                _logger.LogDebug("No pending inventory transfers in queue");
                return;
            }

            _logger.LogInformation("Processing {Count} queued inventory transfers", pendingTransfers.Count);

            foreach (var queueEntry in pendingTransfers)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Cancellation requested, stopping transfer queue processing");
                    break;
                }

                await ProcessSingleTransferAsync(
                    queueEntry,
                    queueService,
                    sapService,
                    stoppingToken);
            }
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task ProcessSingleTransferAsync(
        InventoryTransferQueueEntity queueEntry,
        IInventoryTransferQueueService queueService,
        ISAPServiceLayerClient sapService,
        CancellationToken stoppingToken)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Mark as processing
            await queueService.MarkAsProcessingAsync(queueEntry.Id, stoppingToken);

            _logger.LogInformation(
                "Processing inventory transfer: ExternalRef={ExternalReference}, QueueId={QueueId}, Attempt={Attempt}",
                queueEntry.ExternalReference, queueEntry.Id, queueEntry.RetryCount + 1);

            // Deserialize the transfer request
            var request = JsonSerializer.Deserialize<CreateDesktopTransferRequest>(
                queueEntry.TransferPayload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (request == null)
            {
                throw new InvalidOperationException("Failed to deserialize transfer payload");
            }

            // Build the SAP transfer/request object
            if (queueEntry.IsTransferRequest)
            {
                await PostTransferRequestAsync(queueEntry, request, queueService, sapService, stoppingToken);
            }
            else
            {
                await PostDirectTransferAsync(queueEntry, request, queueService, sapService, stoppingToken);
            }

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation(
                "Inventory transfer processed successfully: ExternalRef={ExternalReference}, Duration={Duration}ms",
                queueEntry.ExternalReference, duration.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex,
                "Failed to process inventory transfer: ExternalRef={ExternalReference}, QueueId={QueueId}, Duration={Duration}ms",
                queueEntry.ExternalReference, queueEntry.Id, duration.TotalMilliseconds);

            await HandleTransferErrorAsync(queueEntry, queueService, ex, stoppingToken);
        }
    }

    private async Task PostTransferRequestAsync(
        InventoryTransferQueueEntity queueEntry,
        CreateDesktopTransferRequest request,
        IInventoryTransferQueueService queueService,
        ISAPServiceLayerClient sapService,
        CancellationToken stoppingToken)
    {
        // Build inventory transfer request DTO for SAP
        var transferRequestDto = new CreateTransferRequestDto
        {
            FromWarehouse = request.FromWarehouse,
            ToWarehouse = request.ToWarehouse,
            DocDate = request.DocDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DueDate = request.DueDate,
            Comments = request.Comments,
            Lines = request.Lines.Select(l => new CreateTransferRequestLineDto
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                ToWarehouseCode = l.WarehouseCode ?? request.ToWarehouse
            }).ToList()
        };

        // Post to SAP as transfer request
        var result = await sapService.CreateInventoryTransferRequestAsync(transferRequestDto, stoppingToken);

        // Update queue with success
        await queueService.UpdateQueueEntryAsync(
            queueEntry.Id,
            InventoryTransferQueueStatus.Completed,
            result.DocEntry.ToString(),
            result.DocNum,
            cancellationToken: stoppingToken);
    }

    private async Task PostDirectTransferAsync(
        InventoryTransferQueueEntity queueEntry,
        CreateDesktopTransferRequest request,
        IInventoryTransferQueueService queueService,
        ISAPServiceLayerClient sapService,
        CancellationToken stoppingToken)
    {
        // Build inventory transfer request for SAP
        var transferDto = new CreateInventoryTransferRequest
        {
            FromWarehouse = request.FromWarehouse,
            ToWarehouse = request.ToWarehouse,
            DocDate = request.DocDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DueDate = request.DueDate,
            Comments = request.Comments,
            Lines = request.Lines.Select(l => new CreateInventoryTransferLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                ToWarehouseCode = l.WarehouseCode ?? request.ToWarehouse
            }).ToList()
        };

        // Post to SAP as direct transfer
        var result = await sapService.CreateInventoryTransferAsync(transferDto, stoppingToken);

        // Update queue with success
        await queueService.UpdateQueueEntryAsync(
            queueEntry.Id,
            InventoryTransferQueueStatus.Completed,
            result.DocEntry.ToString(),
            result.DocNum,
            cancellationToken: stoppingToken);
    }

    private async Task HandleTransferErrorAsync(
        InventoryTransferQueueEntity queueEntry,
        IInventoryTransferQueueService queueService,
        Exception ex,
        CancellationToken stoppingToken)
    {
        var errorMessage = ex.Message;
        if (errorMessage.Length > 1900)
        {
            errorMessage = errorMessage[..1900] + "...";
        }

        // Check if this was the last retry
        var newRetryCount = queueEntry.RetryCount + 1;
        var newStatus = newRetryCount >= queueEntry.MaxRetries
            ? InventoryTransferQueueStatus.RequiresReview
            : InventoryTransferQueueStatus.Failed;

        // Calculate next retry time with exponential backoff
        var backoffSeconds = Math.Pow(2, newRetryCount) * 10; // 20s, 40s, 80s, etc.
        var nextRetryAt = DateTime.UtcNow.AddSeconds(backoffSeconds);

        await queueService.UpdateQueueEntryAsync(
            queueEntry.Id,
            newStatus,
            error: errorMessage,
            cancellationToken: stoppingToken);

        if (newStatus == InventoryTransferQueueStatus.RequiresReview)
        {
            _logger.LogWarning(
                "Inventory transfer marked for review after {RetryCount} attempts: ExternalRef={ExternalReference}",
                newRetryCount, queueEntry.ExternalReference);
        }
        else
        {
            _logger.LogInformation(
                "Inventory transfer will retry at {NextRetry}: ExternalRef={ExternalReference}",
                nextRetryAt, queueEntry.ExternalReference);
        }
    }
}
