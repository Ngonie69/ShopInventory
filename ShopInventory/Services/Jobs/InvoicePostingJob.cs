using System.Text.Json;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that processes queued invoices — fiscalizes them and stores locally.
/// Desktop invoices are NOT posted to SAP individually; they are accumulated and posted as a
/// single consolidated invoice per customer at end-of-day via ConsolidateDailySales.
/// Cadence, clustering and misfire handling are owned by Quartz (see QuartzConfiguration).
/// </summary>
/// <remarks>
/// <b>Van sales are the exception: they are fiscalised and posted here, one invoice per sale.</b>
/// Consolidation deliberately refuses them (see <c>InvoiceQueueService.GetFiscalizedInvoicesAsync</c>),
/// yet this job used to stop at <see cref="InvoiceQueueStatus.Fiscalized"/> for them all the same — so a
/// converted van order was signed, handed to the customer, and never invoiced. They now go through
/// <see cref="VanSaleFiscalFirstPoster"/>, which signs first and posts second. Entries already stranded at
/// Fiscalized are picked up again by the same route, and the device is asked for the receipt they already
/// hold before anything is sent, so none is signed twice.
/// </remarks>
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
        var vanSalePoster = scope.ServiceProvider.GetRequiredService<VanSaleFiscalFirstPoster>();
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
                vanSalePoster,
                vatRate,
                stoppingToken);
        }
    }

    private async Task ProcessSingleInvoiceAsync(
        InvoiceQueueEntity queueEntry,
        IInvoiceQueueService queueService,
        IFiscalizationService? fiscalizationService,
        VanSaleFiscalFirstPoster vanSalePoster,
        decimal vatRate,
        CancellationToken stoppingToken)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Mark as processing
            await queueService.MarkAsProcessingAsync(queueEntry.Id, stoppingToken);

            if (string.Equals(queueEntry.SourceSystem, SaleSourceSystems.VanSales, StringComparison.Ordinal))
            {
                await ProcessVanSaleAsync(queueEntry, queueService, vanSalePoster, stoppingToken);
                return;
            }

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
                    customerDetails: null,
                    paymentType: TenderTypes.ToMoneyType(request.PaymentMethod),
                    cancellationToken: stoppingToken);

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
    /// Fiscalises a queued van sale and posts it to SAP as its own invoice.
    /// </summary>
    /// <remarks>
    /// Always told the sale may already be signed. Every van entry that reached Fiscalized before this path
    /// existed holds a receipt with no row to say so, and the lookup that answers it costs one device call
    /// on a background job with nobody waiting.
    /// </remarks>
    private async Task ProcessVanSaleAsync(
        InvoiceQueueEntity queueEntry,
        IInvoiceQueueService queueService,
        VanSaleFiscalFirstPoster poster,
        CancellationToken stoppingToken)
    {
        var payload = JsonSerializer.Deserialize<CreateStockReservationRequest>(
            queueEntry.InvoicePayload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // The day the sale was made, not the day the job reached it. The receipt was dated then, and an
        // entry stranded for weeks must not be invoiced into a later period than its receipt.
        var saleDay = AuditService.ToCAT(queueEntry.CreatedAt).ToString("yyyy-MM-dd");

        var outcome = await poster.FiscaliseThenPostAsync(
            new VanSaleFiscalFirstRequest(
                queueEntry.ReservationId,
                DocDate: saleDay,
                DocDueDate: saleDay,
                Comments: payload?.Notes,
                MayAlreadyBeFiscalised: true),
            stoppingToken);

        var sale = outcome.Sale;

        if (outcome.Status == VanSaleFiscalFirstStatus.Posted)
        {
            await queueService.UpdateQueueEntryAsync(
                queueEntry.Id,
                InvoiceQueueStatus.Completed,
                outcome.SapDocEntry?.ToString(),
                outcome.SapDocNum,
                null,
                sale?.FiscalDeviceNumber,
                sale?.FiscalReceiptNumber,
                CancellationToken.None);

            _logger.LogInformation(
                "Van sale fiscalised and invoiced: ExternalRef={ExternalReference}, Receipt={Receipt}, DocNum={DocNum}",
                queueEntry.ExternalReference, sale?.FiscalReceiptNumber, outcome.SapDocNum);
            return;
        }

        // Unresolved and unpostable wait for a person. The rest are retried while retries remain, and a
        // sale that is already signed retries only its post, never its receipt.
        var needsPerson = outcome.Status is VanSaleFiscalFirstStatus.FiscalUnresolved
                              or VanSaleFiscalFirstStatus.NotPostable
                          || queueEntry.RetryCount >= queueEntry.MaxRetries - 1;

        var status = needsPerson ? InvoiceQueueStatus.RequiresReview : InvoiceQueueStatus.Failed;
        var error = outcome.IsFiscalised
            ? $"Fiscalised (receipt {sale?.FiscalReceiptNumber}) but not yet invoiced: {outcome.Error}"
            : outcome.Error ?? outcome.Status.ToString();

        await queueService.UpdateQueueEntryAsync(
            queueEntry.Id,
            status,
            null,
            null,
            error,
            outcome.IsFiscalised ? sale?.FiscalDeviceNumber : null,
            outcome.IsFiscalised ? sale?.FiscalReceiptNumber : null,
            CancellationToken.None);

        _logger.LogWarning(
            "Van sale {ExternalReference} stopped at {Outcome} and is now {Status}: {Error}",
            queueEntry.ExternalReference, outcome.Status, status, error);
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
