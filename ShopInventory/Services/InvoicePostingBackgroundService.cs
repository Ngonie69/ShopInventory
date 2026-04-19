using System.Text.Json;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Background service that processes queued invoices — fiscalizes them and stores locally.
/// Invoices are NOT posted to SAP individually; they are accumulated and posted as a
/// single consolidated invoice per customer at end-of-day via ConsolidateDailySales.
/// </summary>
public class InvoicePostingBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InvoicePostingBackgroundService> _logger;
    private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(10);
    private readonly int _batchSize = 5;
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1);
    private int _consecutiveErrors = 0;
    private const int MaxConsecutiveErrors = 10;

    public InvoicePostingBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<InvoicePostingBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Invoice Posting Background Service started - Processing every {Interval}s, Batch size: {BatchSize}",
            _processingInterval.TotalSeconds, _batchSize);

        // Initial delay to let the application fully start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

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
                _logger.LogError(ex, "Error in invoice posting background service (consecutive errors: {Count})",
                    _consecutiveErrors);

                // If too many consecutive errors, back off more aggressively
                if (_consecutiveErrors >= MaxConsecutiveErrors)
                {
                    _logger.LogWarning("Too many consecutive errors, backing off for 60 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                    _consecutiveErrors = 0;
                }
            }

            // Add jitter (±3s) so background services don't all hit SAP simultaneously
            var jitter = Random.Shared.Next(-3000, 3000);
            await Task.Delay(_processingInterval + TimeSpan.FromMilliseconds(jitter), stoppingToken);
        }

        _logger.LogInformation("Invoice Posting Background Service stopped");
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        if (!await _processingSemaphore.WaitAsync(0, stoppingToken))
        {
            _logger.LogDebug("Queue processing already in progress, skipping");
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var queueService = scope.ServiceProvider.GetRequiredService<IInvoiceQueueService>();
            var fiscalizationService = scope.ServiceProvider.GetService<IFiscalizationService>();

            // Get next batch of invoices to process
            var pendingInvoices = await queueService.GetNextBatchForProcessingAsync(_batchSize, stoppingToken);

            if (!pendingInvoices.Any())
            {
                _logger.LogDebug("No pending invoices in queue");
                return;
            }

            _logger.LogInformation("Processing {Count} queued invoices for fiscalization", pendingInvoices.Count);

            foreach (var queueEntry in pendingInvoices)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Cancellation requested, stopping queue processing");
                    break;
                }

                await ProcessSingleInvoiceAsync(
                    queueEntry,
                    queueService,
                    fiscalizationService,
                    stoppingToken);
            }
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private async Task ProcessSingleInvoiceAsync(
        InvoiceQueueEntity queueEntry,
        IInvoiceQueueService queueService,
        IFiscalizationService? fiscalizationService,
        CancellationToken stoppingToken)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Mark as processing
            await queueService.MarkAsProcessingAsync(queueEntry.Id, stoppingToken);

            _logger.LogInformation(
                "Fiscalizing invoice: ExternalRef={ExternalReference}, QueueId={QueueId}, Attempt={Attempt}",
                queueEntry.ExternalReference, queueEntry.Id, queueEntry.RetryCount + 1);

            // Deserialize the invoice request
            var request = JsonSerializer.Deserialize<CreateStockReservationRequest>(
                queueEntry.InvoicePayload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (request == null)
            {
                throw new InvalidOperationException("Failed to deserialize invoice payload");
            }

            // Fiscalize the invoice (if required)
            string? fiscalDeviceNumber = null;
            string? fiscalReceiptNumber = null;

            if (queueEntry.RequiresFiscalization)
            {
                if (fiscalizationService == null)
                {
                    _logger.LogWarning(
                        "Fiscalization required but service not available for {ExternalReference}",
                        queueEntry.ExternalReference);
                    throw new InvalidOperationException("Fiscalization is required but the fiscalization service is not available");
                }

                var invoiceDto = BuildInvoiceDtoFromPayload(queueEntry, request);
                var fiscalResult = await fiscalizationService.FiscalizeInvoiceAsync(
                    invoiceDto,
                    queueEntry.ExternalReference,
                    null,
                    stoppingToken);

                if (fiscalResult.Success)
                {
                    fiscalDeviceNumber = fiscalResult.DeviceSerial;
                    fiscalReceiptNumber = fiscalResult.ReceiptGlobalNo;
                    _logger.LogInformation(
                        "Invoice fiscalized: {ExternalReference}, Receipt: {Receipt}",
                        queueEntry.ExternalReference, fiscalReceiptNumber);
                }
                else
                {
                    _logger.LogError(
                        "Fiscalization failed for {ExternalReference}: {Error}",
                        queueEntry.ExternalReference, fiscalResult.Message ?? fiscalResult.ErrorDetails ?? "Unknown error");
                    throw new InvalidOperationException(
                        $"Fiscalization failed: {fiscalResult.Message ?? fiscalResult.ErrorDetails ?? "Unknown error"}");
                }
            }

            // Mark as Fiscalized — SAP posting happens at end-of-day via ConsolidateDailySales
            await queueService.UpdateQueueEntryAsync(
                queueEntry.Id,
                InvoiceQueueStatus.Fiscalized,
                null, // No SAP DocEntry yet
                null, // No SAP DocNum yet
                null,
                fiscalDeviceNumber,
                fiscalReceiptNumber,
                stoppingToken);

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation(
                "Invoice fiscalized and stored locally: ExternalRef={ExternalReference}, Duration={Duration}ms. Awaiting end-of-day consolidation.",
                queueEntry.ExternalReference, duration.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process invoice: ExternalRef={ExternalReference}, QueueId={QueueId}",
                queueEntry.ExternalReference, queueEntry.Id);

            // Determine if this is a retryable error
            var isRetryable = IsRetryableError(ex);
            var newStatus = isRetryable && queueEntry.RetryCount < queueEntry.MaxRetries - 1
                ? InvoiceQueueStatus.Failed
                : InvoiceQueueStatus.RequiresReview;

            await queueService.UpdateQueueEntryAsync(
                queueEntry.Id,
                newStatus,
                null,
                null,
                ex.Message,
                null,
                null,
                stoppingToken);

            if (newStatus == InvoiceQueueStatus.RequiresReview)
            {
                _logger.LogWarning(
                    "Invoice marked for review: ExternalRef={ExternalReference}, Error={Error}",
                    queueEntry.ExternalReference, ex.Message);
            }
            else
            {
                var nextRetry = DateTime.UtcNow.AddSeconds(30 * Math.Pow(2, queueEntry.RetryCount));
                _logger.LogWarning(
                    "Invoice will retry: ExternalRef={ExternalReference}, NextRetry={NextRetry}",
                    queueEntry.ExternalReference, nextRetry);
            }
        }
    }

    /// <summary>
    /// Builds an InvoiceDto from queue entry and deserialized payload data
    /// for pre-SAP fiscalization.
    /// </summary>
    private static InvoiceDto BuildInvoiceDtoFromPayload(
        InvoiceQueueEntity queueEntry,
        CreateStockReservationRequest request)
    {
        var lines = request.Lines.Select((l, i) => new InvoiceLineDto
        {
            LineNum = l.LineNum,
            ItemCode = l.ItemCode,
            ItemDescription = l.ItemDescription,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            LineTotal = l.Quantity * l.UnitPrice * (1 - l.DiscountPercent / 100m),
            WarehouseCode = l.WarehouseCode,
            DiscountPercent = l.DiscountPercent,
            UoMCode = l.UoMCode
        }).ToList();

        var docTotal = lines.Sum(l => l.LineTotal);

        // Approximate VAT sum (15.5% VAT-exclusive on line totals)
        // REVMax recalculates from per-item TAXR, this is for the header summary
        var vatSum = docTotal * 0.155m;

        return new InvoiceDto
        {
            DocEntry = 0,
            DocNum = 0,
            CardCode = request.CardCode,
            CardName = request.CardName,
            DocDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DocCurrency = request.Currency ?? queueEntry.Currency,
            DocTotal = docTotal,
            VatSum = vatSum,
            Comments = request.Notes,
            Lines = lines
        };
    }

    private static bool IsRetryableError(Exception ex)
    {
        // Network/timeout errors are retryable
        if (ex is HttpRequestException ||
            ex is TaskCanceledException ||
            ex is TimeoutException)
        {
            return true;
        }

        // SAP session errors are retryable
        var message = ex.Message.ToLowerInvariant();
        if (message.Contains("session") ||
            message.Contains("timeout") ||
            message.Contains("connection") ||
            message.Contains("unavailable") ||
            message.Contains("temporarily"))
        {
            return true;
        }

        // Business logic errors (stock, validation) are not retryable
        if (message.Contains("insufficient") ||
            message.Contains("not found") ||
            message.Contains("invalid") ||
            message.Contains("already exists"))
        {
            return false;
        }

        // Default to retryable for unknown errors
        return true;
    }
}
