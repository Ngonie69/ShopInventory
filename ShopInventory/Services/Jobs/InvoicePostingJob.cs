using System.Text.Json;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that processes queued invoices — fiscalizes them and stores locally.
/// Invoices are NOT posted to SAP individually; they are accumulated and posted as a
/// single consolidated invoice per customer at end-of-day via ConsolidateDailySales.
/// Cadence, clustering and misfire handling are owned by Quartz (see QuartzConfiguration).
/// </summary>
[DisallowConcurrentExecution]
public sealed class InvoicePostingJob : IJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InvoicePostingJob> _logger;
    private readonly int _batchSize = 5;

    public InvoicePostingJob(
        IServiceProvider serviceProvider,
        ILogger<InvoicePostingJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        await ProcessQueueAsync(context.CancellationToken);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var queueService = scope.ServiceProvider.GetRequiredService<IInvoiceQueueService>();
        var fiscalizationService = scope.ServiceProvider.GetService<IFiscalizationService>();
        var vatRate = scope.ServiceProvider
            .GetRequiredService<IOptions<TaxSettings>>().Value.VatRate;

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
                vatRate,
                stoppingToken);
        }
    }

    private async Task ProcessSingleInvoiceAsync(
        InvoiceQueueEntity queueEntry,
        IInvoiceQueueService queueService,
        IFiscalizationService? fiscalizationService,
        decimal vatRate,
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

                var invoiceDto = BuildInvoiceDtoFromPayload(queueEntry, request, vatRate);

                // Pre-SAP: this invoice does not exist in SAP yet, so it is fiscalised from a full
                // payload under its external reference. That reference is the receipt's permanent
                // fiscal identity and must stay byte-identical across every retry of this entry.
                var fiscalResult = await fiscalizationService.FiscalizePreSapInvoiceAsync(
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
                else if (fiscalResult.RequiresReconciliation)
                {
                    // The fiscal outcome is unresolved: a receipt may already exist at FDMS. Retrying
                    // could produce a second one, and a fiscal receipt cannot be withdrawn. Park it for
                    // a human instead of letting IsRetryableError default this to retryable.
                    _logger.LogError(
                        "Fiscalization of {ExternalReference} is unresolved ({ErrorCode}). "
                        + "Parked for reconciliation — do not resubmit before checking the fiscal console.",
                        queueEntry.ExternalReference,
                        fiscalResult.ErrorCode);

                    await queueService.UpdateQueueEntryAsync(
                        queueEntry.Id,
                        InvoiceQueueStatus.RequiresReview,
                        null,
                        null,
                        fiscalResult.Message ?? fiscalResult.ErrorDetails,
                        null,
                        null,
                        stoppingToken);

                    return;
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
        CreateStockReservationRequest request,
        decimal vatRate)
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

        // Approximate VAT sum for the header summary only. The fiscalisation platform recalculates
        // tax per line from the line's tax id, so this figure never reaches FDMS.
        var vatSum = docTotal * vatRate;

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
