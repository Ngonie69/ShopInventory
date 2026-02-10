using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Background service that processes queued invoices and posts them to SAP in batches
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

            await Task.Delay(_processingInterval, stoppingToken);
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
            var sapService = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();
            var reservationService = scope.ServiceProvider.GetRequiredService<IStockReservationService>();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Get next batch of invoices to process
            var pendingInvoices = await queueService.GetNextBatchForProcessingAsync(_batchSize, stoppingToken);

            if (!pendingInvoices.Any())
            {
                _logger.LogDebug("No pending invoices in queue");
                return;
            }

            _logger.LogInformation("Processing {Count} queued invoices", pendingInvoices.Count);

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
                    sapService,
                    reservationService,
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
        ISAPServiceLayerClient sapService,
        IStockReservationService reservationService,
        CancellationToken stoppingToken)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Mark as processing
            await queueService.MarkAsProcessingAsync(queueEntry.Id, stoppingToken);

            _logger.LogInformation(
                "Processing invoice: ExternalRef={ExternalReference}, QueueId={QueueId}, Attempt={Attempt}",
                queueEntry.ExternalReference, queueEntry.Id, queueEntry.RetryCount + 1);

            // Deserialize the invoice request
            var request = JsonSerializer.Deserialize<CreateStockReservationRequest>(
                queueEntry.InvoicePayload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (request == null)
            {
                throw new InvalidOperationException("Failed to deserialize invoice payload");
            }

            // Get the reservation to ensure stock is still reserved
            var reservation = await reservationService.GetReservationAsync(
                queueEntry.ReservationId, stoppingToken);

            if (reservation == null)
            {
                throw new InvalidOperationException($"Reservation {queueEntry.ReservationId} not found");
            }

            if (reservation.Status != ReservationStatus.Pending &&
                reservation.Status != ReservationStatus.Confirmed)
            {
                throw new InvalidOperationException(
                    $"Reservation {queueEntry.ReservationId} is in status {reservation.Status}, cannot post invoice");
            }

            // Build SAP invoice document using existing CreateInvoiceRequest
            var invoiceLines = new List<CreateInvoiceLineRequest>();

            foreach (var line in request.Lines)
            {
                var sapLine = new CreateInvoiceLineRequest
                {
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    WarehouseCode = line.WarehouseCode,
                    UnitPrice = line.UnitPrice,
                    TaxCode = line.TaxCode,
                    DiscountPercent = line.DiscountPercent,
                    UoMCode = line.UoMCode,
                    AutoAllocateBatches = line.AutoAllocateBatches
                };

                // Add batch allocations if present
                if (line.BatchNumbers?.Any() == true)
                {
                    sapLine.BatchNumbers = line.BatchNumbers.Select(b => new BatchNumberRequest
                    {
                        BatchNumber = b.BatchNumber,
                        Quantity = b.Quantity
                    }).ToList();
                }

                invoiceLines.Add(sapLine);
            }

            var sapInvoice = new CreateInvoiceRequest
            {
                CardCode = queueEntry.CustomerCode,
                DocDate = DateTime.Now.ToString("yyyy-MM-dd"),
                DocDueDate = request.DocDueDate?.ToString("yyyy-MM-dd") ?? DateTime.Now.AddDays(30).ToString("yyyy-MM-dd"),
                Lines = invoiceLines,
                Comments = $"Desktop Invoice: {queueEntry.ExternalReference}",
                NumAtCard = queueEntry.ExternalReference,
                SalesPersonCode = request.SalesPersonCode
            };

            // Post to SAP
            _logger.LogDebug("Posting invoice to SAP: {ExternalReference}", queueEntry.ExternalReference);

            var invoice = await sapService.CreateInvoiceAsync(sapInvoice, stoppingToken);

            // Invoice was created successfully (if it fails, an exception is thrown)
            _logger.LogInformation(
                "Invoice posted to SAP: ExternalRef={ExternalReference}, DocEntry={DocEntry}, DocNum={DocNum}",
                queueEntry.ExternalReference, invoice.DocEntry, invoice.DocNum);

            // Handle fiscalization if required
            string? fiscalDeviceNumber = null;
            string? fiscalReceiptNumber = null;

            if (queueEntry.RequiresFiscalization)
            {
                try
                {
                    var fiscalResult = await FiscalizeInvoiceAsync(
                        scope: _serviceProvider.CreateScope(),
                        docEntry: invoice.DocEntry,
                        stoppingToken);

                    if (fiscalResult != null && fiscalResult.Success)
                    {
                        fiscalDeviceNumber = fiscalResult.DeviceSerial;
                        fiscalReceiptNumber = fiscalResult.ReceiptGlobalNo;
                        _logger.LogInformation(
                            "Invoice fiscalized: {ExternalReference}, Receipt: {Receipt}",
                            queueEntry.ExternalReference, fiscalReceiptNumber);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Fiscalization failed for {ExternalReference}: {Error}",
                            queueEntry.ExternalReference, fiscalResult?.Message ?? fiscalResult?.ErrorDetails ?? "Unknown error");
                    }
                }
                catch (Exception fiscalEx)
                {
                    _logger.LogWarning(fiscalEx,
                        "Fiscalization error for {ExternalReference} - invoice posted but not fiscalized",
                        queueEntry.ExternalReference);
                }
            }

            // Update queue entry as completed
            await queueService.UpdateQueueEntryAsync(
                queueEntry.Id,
                InvoiceQueueStatus.Completed,
                invoice.DocEntry.ToString(),
                invoice.DocNum,
                null,
                fiscalDeviceNumber,
                fiscalReceiptNumber,
                stoppingToken);

            // Confirm the reservation (mark as confirmed in our system)
            // The ConfirmReservationRequest just needs the ID - SAP details are already in queue entry
            await reservationService.ConfirmReservationAsync(new ConfirmReservationRequest
            {
                ReservationId = queueEntry.ReservationId,
                Fiscalize = false // Already fiscalized above if needed
            }, stoppingToken);

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation(
                "Invoice processing completed: ExternalRef={ExternalReference}, Duration={Duration}ms",
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

    private async Task<FiscalizationResult?> FiscalizeInvoiceAsync(
        IServiceScope scope,
        int docEntry,
        CancellationToken stoppingToken)
    {
        try
        {
            // Get the invoice from SAP first
            var sapClient = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();
            var fiscalizationService = scope.ServiceProvider.GetService<IFiscalizationService>();

            if (fiscalizationService == null)
            {
                _logger.LogDebug("Fiscalization service not available");
                return null;
            }

            // Get invoice details from SAP for fiscalization
            var invoice = await sapClient.GetInvoiceByDocEntryAsync(docEntry, stoppingToken);
            if (invoice == null)
            {
                return new FiscalizationResult
                {
                    Success = false,
                    Message = $"Could not retrieve invoice {docEntry} for fiscalization"
                };
            }

            // Convert to InvoiceDto for fiscalization service
            var invoiceDto = new InvoiceDto
            {
                DocEntry = invoice.DocEntry,
                DocNum = invoice.DocNum,
                CardCode = invoice.CardCode ?? "",
                CardName = invoice.CardName ?? "",
                DocDate = invoice.DocDate ?? DateTime.Now.ToString("yyyy-MM-dd"),
                DocTotal = invoice.DocTotal,
                VatSum = invoice.VatSum,
                DocCurrency = invoice.DocCurrency ?? "USD"
            };

            return await fiscalizationService.FiscalizeInvoiceAsync(invoiceDto, null, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fiscalization failed for DocEntry {DocEntry}", docEntry);
            return new FiscalizationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
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
