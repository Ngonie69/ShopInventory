using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;

/// <summary>
/// Turns an approved sales order into a reserved, queued invoice.
/// </summary>
/// <remarks>
/// A conversion that names its own reference is idempotent on it: the van handset mints
/// <c>van_order</c> once per conversion and sends it on every retry, and a retry after a lost 202 is
/// answered with that 202 rather than with an error. Two guards hold that, one per horizon:
///
/// <para>The idempotency claim (per caller and reference) settles a race and replays the exact body
/// for as long as the claim lives. The invoice queue entry, which carries the reference and the
/// order it came from, answers a retry that arrives after the claim has expired — by then the
/// order is Fulfilled and the status check alone would refuse it.</para>
///
/// <para><b>A van conversion is signed before it is answered</b>
/// (<see cref="ConvertSalesOrderToInvoiceCommand.SignBeforeAnswering"/>), exactly as a direct van sale is:
/// reserve, fiscalise through <see cref="VanSaleFiscalFirstPoster"/>, and hand the invoice to the queue
/// already <see cref="InvoiceQueueStatus.Fiscalized"/> for <c>PostQueuedVanInvoices</c> to post on the
/// order's base. The rep is at the counter, and the receipt is what they hand over; queued unsigned, the
/// invoice was signed minutes later by the queue and the handset had nothing to print. Every other
/// caller keeps the unsigned queue.</para>
/// </remarks>
public sealed class ConvertSalesOrderToInvoiceHandler(
    ISalesOrderService salesOrderService,
    IStockReservationService reservationService,
    IInvoiceQueueService queueService,
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<ConvertSalesOrderToInvoiceHandler> logger,
    ApplicationDbContext db,
    VanSaleFiscalFirstPoster poster
) : IRequestHandler<ConvertSalesOrderToInvoiceCommand, ErrorOr<ConvertSalesOrderToInvoiceResponseDto>>
{
    internal const string IdempotencyScope = "sales-order-invoice-conversion";

    private const string AcceptedMessage =
        "Sales order converted to invoice and queued for SAP posting. Poll the status endpoint to check completion.";

    public Task<ErrorOr<ConvertSalesOrderToInvoiceResponseDto>> Handle(
        ConvertSalesOrderToInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        // The last point at which the caller going away stops the conversion: nothing is claimed,
        // reserved or signed yet, so there is nothing to finish.
        cancellationToken.ThrowIfCancellationRequested();

        return ConvertAsync(command);
    }

    /// <summary>
    /// The conversion, from its claim to its answer. Once started it runs to the end.
    /// </summary>
    /// <remarks>
    /// <para><b>It takes no token, and that is the guard.</b> ASP.NET binds the request's token to
    /// <c>HttpContext.RequestAborted</c>, and a van handset hangs up after 30 seconds. On 2026-10-07 a
    /// conversion waiting on SAP inside its reservation outlived that: the hang-up cancelled it mid-way, the
    /// server answered "The operation was canceled" to nobody, and the rep was told "Not confirmed" for a
    /// conversion the server had walked away from. Run to the end instead, the work is done once: the claim
    /// is completed with the answer, and the handset's resend under the same reference is handed it.</para>
    ///
    /// <para>Nothing waits forever for want of a token. Every SAP read is bounded by the client's own
    /// budget, and the fiscal device by its own timeout; the request's token only ever added a deadline the
    /// work did not need and the caller had already stopped waiting for.</para>
    /// </remarks>
    private async Task<ErrorOr<ConvertSalesOrderToInvoiceResponseDto>> ConvertAsync(ConvertSalesOrderToInvoiceCommand command)
    {
        // Deliberately and literally CancellationToken.None — see the remarks. Held in a local so that a
        // future edit adding a call here cannot quietly reintroduce the request token.
        var unstoppable = CancellationToken.None;
        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            var request = command.Request;
            var suppliedReference = string.IsNullOrWhiteSpace(request.ExternalReferenceId)
                ? null
                : request.ExternalReferenceId.Trim();

            if (suppliedReference is not null)
            {
                var acquired = await idempotencyRequestStore.TryAcquireAsync<ConvertSalesOrderToInvoiceResponseDto>(
                    IdempotencyScope,
                    $"{command.CreatedBy ?? "anonymous"}:{suppliedReference}",
                    Fingerprint(request),
                    unstoppable);

                switch (acquired.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null:
                        logger.LogInformation(
                            "Replaying sales order conversion {ExternalRef} for order {OrderId}",
                            suppliedReference, request.SalesOrderId);
                        return acquired.Response;
                    case IdempotencyAcquireOutcome.InProgress:
                        return Errors.Idempotency.RequestInProgress("sales order conversion");
                    case IdempotencyAcquireOutcome.RequestMismatch:
                        return Errors.Idempotency.RequestMismatch("sales order conversion");
                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquired.RequestId;
                        release = true;
                        break;
                }
            }

            var order = await salesOrderService.GetByIdFromLocalAsync(request.SalesOrderId, unstoppable);

            if (order == null)
                return Errors.DesktopIntegration.ValidationFailed(
                    $"Sales order with ID {request.SalesOrderId} not found");

            // Before the status check: the first conversion is what moved the order out of Approved,
            // so a retry that outlived the claim would otherwise be refused for having succeeded.
            if (suppliedReference is not null)
            {
                var queued = await queueService.GetQueueStatusAsync(suppliedReference, unstoppable);
                if (queued is not null)
                {
                    if (queued.SalesOrderId != order.Id)
                        return Errors.DesktopIntegration.ValidationFailed(
                            $"Reference '{suppliedReference}' has already been used for a different invoice");

                    logger.LogInformation(
                        "Sales order {OrderNumber} was already converted under {ExternalRef} (QueueId={QueueId}); replaying",
                        order.OrderNumber, suppliedReference, queued.QueueId);

                    // A conversion signed in its request is answered with its receipt again, so a resend
                    // after a lost reply still prints. Read off the receipt row, never by signing: the
                    // device is not asked anything here.
                    var signedReplay = await SignedReplayAsync(order, suppliedReference, queued);
                    if (signedReplay is { IsError: true })
                        return signedReplay.Value.Errors;

                    var replay = signedReplay?.Value
                        ?? Accepted(order, suppliedReference, queued.ReservationId, queued.QueueId, queued.Status);
                    release = !await CompleteAsync(idempotencyRequestId, replay);
                    return replay;
                }
            }

            if (order.Status != SalesOrderStatus.Approved)
                return Errors.DesktopIntegration.ValidationFailed(
                    $"Only approved orders can be converted to invoices. Current status: {order.StatusName}");

            if (!order.Lines.Any())
                return Errors.DesktopIntegration.ValidationFailed("Sales order has no line items");

            var externalRef = suppliedReference ??
                $"SO-CONV-{order.OrderNumber}-{Guid.NewGuid().ToString()[..8]}";

            logger.LogInformation(
                "Converting sales order {OrderNumber} (ID: {OrderId}) to invoice: {ExternalRef}",
                order.OrderNumber, order.Id, externalRef);

            // Build invoice lines - use provided lines or map from order
            List<CreateDesktopInvoiceLineRequest> invoiceLines;

            if (request.Lines != null && request.Lines.Any())
            {
                invoiceLines = request.Lines;
                logger.LogInformation(
                    "Using {Count} custom lines for conversion (original order had {OriginalCount} lines)",
                    invoiceLines.Count, order.Lines.Count);
            }
            else
            {
                invoiceLines = order.Lines.Select((line, idx) => new CreateDesktopInvoiceLineRequest
                {
                    LineNum = idx,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    WarehouseCode = line.WarehouseCode ?? order.WarehouseCode ?? "",
                    UoMCode = line.UoMCode,
                    DiscountPercent = line.DiscountPercent,
                    CostCentreCode = line.CostCentreCode,
                    AutoAllocateBatches = true
                }).ToList();
            }

            if (!invoiceLines.Any())
                return Errors.DesktopIntegration.ValidationFailed("No invoice lines to process");

            // Create stock reservation
            var reservationRequest = new CreateStockReservationRequest
            {
                ExternalReference = externalRef,
                ExternalReferenceId = externalRef,
                SourceSystem = request.SourceSystem ?? "DESKTOP_APP",
                DocumentType = ReservationDocumentType.Invoice,
                CardCode = order.CardCode,
                CardName = order.CardName,
                Currency = request.DocCurrency ?? order.Currency,
                PaymentMethod = request.PaymentMethod,
                ReservationDurationMinutes = 60,
                RequiresFiscalization = request.Fiscalize,
                Notes = request.Comments ?? $"Converted from Sales Order {order.OrderNumber}",
                Lines = invoiceLines.Select(l => new CreateStockReservationLineRequest
                {
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    UoMCode = l.UoMCode,
                    WarehouseCode = l.WarehouseCode,
                    UnitPrice = l.UnitPrice ?? 0,
                    TaxCode = l.TaxCode,
                    DiscountPercent = l.DiscountPercent ?? 0,
                    CostCentreCode = l.CostCentreCode,
                    BatchNumbers = l.BatchNumbers?.Select(b => new ReservationBatchRequest
                    {
                        BatchNumber = b.BatchNumber,
                        Quantity = b.Quantity
                    }).ToList(),
                    AutoAllocateBatches = l.AutoAllocateBatches
                }).ToList()
            };

            var reservationResult = await reservationService.CreateReservationAsync(
                reservationRequest, command.CreatedBy, unstoppable);

            if (!reservationResult.Success)
            {
                // A reservation under this reference that is no longer pending — cancelled when its
                // queueing failed, or expired after its hour; a pending one is handed back, not refused.
                // Not forwarded in the service's words, which say the reference "already exists": the
                // handset reads that as the invoice existing, records the order as converted and the
                // van's stock as gone, for an attempt that lapsed. Worded as the refusal it is, the
                // handset retires the reference and the resend reserves afresh under a new one.
                if (reservationResult.Errors?.Any(error => error.ErrorCode == ReservationErrorCode.DuplicateReference) == true)
                {
                    logger.LogWarning(
                        "Sales order {OrderNumber} conversion {ExternalRef} found its reservation no longer pending; refusing so the handset re-references",
                        order.OrderNumber, externalRef);

                    return Errors.DesktopIntegration.ReservationFailed(
                        $"An earlier attempt to convert this order under reference '{externalRef}' lapsed " +
                        "before it was invoiced. Convert the order again.");
                }

                // The reservation's own reasons, as the direct van sale answers them: which item, in
                // which warehouse, how many were asked for and how many there are. This used to log
                // them and answer "insufficient stock or batch allocation error", which the handset
                // shows behind "Error:" and nothing else — so a rep with nine lines on the screen was
                // told one of them was short and not which, and resent the lot to find out.
                var reasons = (reservationResult.Errors ?? [])
                    .Select(error => error.Message)
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .ToList();

                var refusal = string.Join(
                    "; ",
                    new[] { reservationResult.Message }.Concat(reasons).Where(m => !string.IsNullOrWhiteSpace(m)));

                logger.LogWarning(
                    "Stock reservation failed for sales order {OrderNumber} conversion: {Errors}",
                    order.OrderNumber,
                    refusal);

                return Errors.DesktopIntegration.ReservationFailed(
                    string.IsNullOrWhiteSpace(refusal)
                        ? "Stock reservation failed — insufficient stock or batch allocation error"
                        : refusal);
            }

            var reservationId = reservationResult.Reservation!.ReservationId;

            if (command.SignBeforeAnswering)
            {
                var signed = await SignThenQueueAsync(
                    command, order, reservationRequest, reservationId, externalRef);

                if (!signed.IsError)
                    release = !await CompleteAsync(idempotencyRequestId, signed.Value);

                return signed;
            }

            // Queue the invoice for batch posting to SAP
            var queueResult = await queueService.EnqueueInvoiceAsync(
                reservationRequest,
                reservationId,
                command.CreatedBy,
                unstoppable,
                // So consolidation can base the invoice on the order's SAP document.
                salesOrderId: order.Id);

            // The reservation service hands a second request under one reference the same pending
            // reservation, so a queue entry already holding it is this conversion's own, queued by a
            // concurrent attempt. Cancelling here would strip the reservation from the invoice that
            // is about to post; it is the success it looks like.
            var alreadyQueuedWithThisReservation = !queueResult.Success
                && queueResult.ErrorCode == "ALREADY_QUEUED"
                && string.Equals(queueResult.ReservationId, reservationId, StringComparison.Ordinal);

            if (alreadyQueuedWithThisReservation)
            {
                logger.LogInformation(
                    "Sales order {OrderNumber} conversion {ExternalRef} was already queued (QueueId={QueueId}) " +
                    "with the same reservation; treating as success",
                    order.OrderNumber, externalRef, queueResult.QueueId);
            }
            else if (!queueResult.Success)
            {
                await reservationService.CancelReservationAsync(new CancelReservationRequest
                {
                    ReservationId = reservationId,
                    Reason = $"Failed to queue invoice from SO conversion: {queueResult.ErrorMessage}"
                }, unstoppable);

                return Errors.DesktopIntegration.InvoiceCreationFailed(
                    queueResult.ErrorMessage ?? "Failed to queue invoice for SAP posting");
            }

            // Mark the sales order as fulfilled
            try
            {
                await salesOrderService.MarkAsFulfilledAsync(order.Id, null, unstoppable);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to mark sales order {OrderId} as fulfilled after queuing invoice. " +
                    "Invoice is still queued and will be processed.",
                    order.Id);
            }

            logger.LogInformation(
                "Sales order {OrderNumber} converted to queued invoice: ExternalRef={ExternalRef}, " +
                "ReservationId={ReservationId}, QueueId={QueueId}",
                order.OrderNumber, externalRef, reservationId, queueResult.QueueId);

            var response = Accepted(order, externalRef, reservationId, queueResult.QueueId, "Pending");
            release = !await CompleteAsync(idempotencyRequestId, response);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error converting sales order to invoice");
            return Errors.DesktopIntegration.InvoiceCreationFailed(ex.Message);
        }
        finally
        {
            // A conversion that did not complete gives its claim back, so the retry can try again.
            if (release && idempotencyRequestId.HasValue)
            {
                try { await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to release the sales order conversion claim"); }
            }
        }
    }

    /// <returns>False when the result could not be recorded and the claim should be released.</returns>
    /// <remarks>
    /// The invoice is queued whether or not this lands, so a failure must not turn the 202 into an
    /// error. The claim is released instead, and a retry is answered from the queue entry.
    /// </remarks>
    private async Task<bool> CompleteAsync(
        long? idempotencyRequestId,
        ConvertSalesOrderToInvoiceResponseDto response)
    {
        if (!idempotencyRequestId.HasValue)
            return true;

        try
        {
            await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, response, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record the sales order conversion result for replay");
            return false;
        }
    }

    /// <summary>
    /// Fiscalises the conversion and hands it to the queue signed — the direct van sale's path, keeping the
    /// sales order the invoice is based on.
    /// </summary>
    /// <remarks>
    /// <para><b>What the rep is told</b> follows <c>CreateVanSalesDirectInvoiceHandler</c>. Until the device
    /// signs, the conversion can still be refused, and the reservation is left holding so the same conversion
    /// sent again finds it. Once it signs it is never refused: the receipt is answered, and the queue posts
    /// the invoice on the order's base. A device that cannot say whether it signed is queued for a person
    /// and the rep is told not to convert again.</para>
    ///
    /// <para><b>Not posted in the request.</b> The poster's own post has no base order, so an invoice posted
    /// there would not close the sales order in SAP; the queue's does. Only when the queue would not take
    /// the entry at all is it posted here unlinked — the direct sale's fallback, and better than an invoice
    /// nothing will ever post.</para>
    ///
    /// <para>Nothing here takes the request's token, the device's question included (see
    /// <see cref="ConvertAsync"/>). From a signed receipt on, it is the record of a sale that happened, and
    /// the handset going away must not stop it being written.</para>
    /// </remarks>
    private async Task<ErrorOr<ConvertSalesOrderToInvoiceResponseDto>> SignThenQueueAsync(
        ConvertSalesOrderToInvoiceCommand command,
        SalesOrderDto order,
        CreateStockReservationRequest reservationRequest,
        string reservationId,
        string externalRef)
    {
        var request = command.Request;
        var createdBy = command.CreatedBy ?? "anonymous";
        var persist = CancellationToken.None;

        var fiscalFirst = new VanSaleFiscalFirstRequest(
            reservationId,
            DocDate: request.DocDate,
            DocDueDate: request.DocDueDate,
            NumAtCard: request.NumAtCard,
            Comments: reservationRequest.Notes,
            PostToSapNow: false);

        var outcome = await poster.FiscaliseThenPostAsync(fiscalFirst, persist);

        switch (outcome.Status)
        {
            case VanSaleFiscalFirstStatus.Posted:
                await MarkFulfilledAsync(order);
                return Signed(order, externalRef, reservationId, outcome, null);

            case VanSaleFiscalFirstStatus.AwaitingSap:
            {
                var queued = await queueService.EnqueueSignedVanSaleAsync(
                    reservationRequest, reservationId, createdBy, outcome, order.Id, persist);

                if (queued is null && outcome.Deferred)
                {
                    logger.LogError(
                        "Converted sales order {OrderNumber} is fiscalised under {ExternalRef} but the queue would " +
                        "not take its invoice; posting it now, without the link to the order",
                        order.OrderNumber, externalRef);

                    outcome = await poster.FiscaliseThenPostAsync(
                        fiscalFirst with { MayAlreadyBeFiscalised = true, PostToSapNow = true },
                        persist);

                    if (outcome.Status == VanSaleFiscalFirstStatus.AwaitingSap)
                    {
                        queued = await queueService.EnqueueSignedVanSaleAsync(
                            reservationRequest, reservationId, createdBy, outcome, order.Id, persist);
                    }
                }

                await MarkFulfilledAsync(order);

                logger.LogInformation(
                    "Sales order {OrderNumber} converted and fiscalised: ExternalRef={ExternalRef}, " +
                    "ReservationId={ReservationId}, QueueId={QueueId}",
                    order.OrderNumber, externalRef, reservationId, queued?.QueueId);

                return Signed(order, externalRef, reservationId, outcome, queued);
            }

            case VanSaleFiscalFirstStatus.FiscalUnresolved:
                // Queued straight to review, holding its stock, and the order with it: the receipt may be in
                // the customer's hand, and an order still Approved could be converted a second time.
                await queueService.EnqueueSignedVanSaleAsync(
                    reservationRequest, reservationId, createdBy, outcome, order.Id, persist);
                await MarkFulfilledAsync(order);
                return Errors.DesktopIntegration.ConversionFiscalOutcomeUnknown;

            case VanSaleFiscalFirstStatus.FiscalUnchecked:
                return Errors.DesktopIntegration.ConversionFiscalDeviceUnavailable;

            case VanSaleFiscalFirstStatus.FiscalFailed:
                return Errors.DesktopIntegration.ConversionFiscalisationFailed(outcome.Error);

            default:
                return Errors.DesktopIntegration.ValidationFailed(outcome.Error ?? "This invoice cannot be raised.");
        }
    }

    /// <summary>
    /// The answer to a resend of a conversion that was signed in its request, read off its receipt row.
    /// </summary>
    /// <returns>
    /// Null when no signed receipt stands behind the queue entry — a conversion queued unsigned, by a caller
    /// that does not sign or by this server before it signed — which is replayed as it always was.
    /// </returns>
    private async Task<ErrorOr<ConvertSalesOrderToInvoiceResponseDto>?> SignedReplayAsync(
        SalesOrderDto order,
        string reference,
        InvoiceQueueStatusDto queued)
    {
        var sale = await db.DesktopSales
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.ExternalReferenceId == reference && s.SourceSystem == SaleSourceSystems.VanSalesOnline,
                CancellationToken.None);

        if (sale is null)
            return null;

        if (sale.FiscalizationRequiresReconciliation)
            return (ErrorOr<ConvertSalesOrderToInvoiceResponseDto>)Errors.DesktopIntegration.ConversionFiscalOutcomeUnknown;

        if (sale.FiscalizationStatus != DesktopSaleFiscalizationStatus.Success)
            return null;

        var posted = sale.SapDocNum.HasValue;

        return Signed(
            order,
            reference,
            queued.ReservationId,
            new VanSaleFiscalFirstOutcome(
                posted ? VanSaleFiscalFirstStatus.Posted : VanSaleFiscalFirstStatus.AwaitingSap,
                sale,
                sale.SapDocEntry,
                sale.SapDocNum,
                Deferred: !posted),
            new InvoiceQueueResultDto
            {
                Success = true,
                ReservationId = queued.ReservationId,
                QueueId = queued.QueueId,
                ExternalReference = reference,
                Status = queued.Status
            });
    }

    /// <summary>
    /// Marks the order fulfilled. Advisory, as it always was here: the invoice stands either way.
    /// </summary>
    private async Task MarkFulfilledAsync(SalesOrderDto order)
    {
        try
        {
            await salesOrderService.MarkAsFulfilledAsync(order.Id, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to mark sales order {OrderId} as fulfilled after fiscalising its invoice. " +
                "The invoice is still queued and will be processed.",
                order.Id);
        }
    }

    /// <summary>
    /// A signed conversion's answer: the direct van sale's (<c>MapFiscalFirstResponse</c>), on the
    /// conversion's own reply.
    /// </summary>
    private static ConvertSalesOrderToInvoiceResponseDto Signed(
        SalesOrderDto order,
        string externalReference,
        string? reservationId,
        VanSaleFiscalFirstOutcome outcome,
        InvoiceQueueResultDto? queued)
    {
        var sale = outcome.Sale;
        var posted = outcome.Status == VanSaleFiscalFirstStatus.Posted;

        return new ConvertSalesOrderToInvoiceResponseDto
        {
            Success = true,
            Message = posted
                ? "Fiscalised and invoiced."
                : outcome.Deferred
                    ? "Fiscalised. The invoice is being posted to SAP."
                    : "Fiscalised. SAP is not taking invoices right now, so the invoice will be posted automatically.",
            SalesOrderId = order.Id,
            SalesOrderNumber = order.OrderNumber,
            ExternalReference = externalReference,
            ReservationId = reservationId,
            QueueId = queued?.QueueId,
            Status = posted ? "Completed" : queued?.Status ?? InvoiceQueueStatus.Fiscalized.ToString(),
            EstimatedProcessingSeconds = posted ? 0 : 30,
            SaleNumber = sale is { Id: > 0 } ? DesktopSaleNumber.Format(sale.Id) : null,
            WasQueued = !posted,
            SapDocEntry = outcome.SapDocEntry,
            SapDocNum = outcome.SapDocNum,
            VerificationCode = sale?.FiscalVerificationCode,
            QrCode = sale?.FiscalQRCode,
            FiscalDay = sale?.FiscalDayNo,
            ReceiptGlobalNo = sale?.FiscalReceiptNumber,
            DeviceSerial = sale?.FiscalDeviceNumber
        };
    }

    /// <summary>
    /// What a retry must repeat to count as the same conversion.
    /// </summary>
    /// <remarks>
    /// Not the whole request: the van mapper stamps <c>DocDate</c> with today when the handset sends
    /// no due date, so a retry across midnight would otherwise read as a different conversion.
    /// </remarks>
    private static object Fingerprint(ConvertSalesOrderToInvoiceRequest request) => new
    {
        request.SalesOrderId,
        ExternalReferenceId = request.ExternalReferenceId?.Trim(),
        request.DocCurrency,
        request.PaymentMethod,
        request.Fiscalize,
        Lines = request.Lines?.Select(line => new
        {
            line.ItemCode,
            line.Quantity,
            line.UnitPrice,
            line.WarehouseCode,
            line.UoMCode,
            line.DiscountPercent
        })
    };

    private static ConvertSalesOrderToInvoiceResponseDto Accepted(
        SalesOrderDto order,
        string externalReference,
        string? reservationId,
        int? queueId,
        string status) => new()
    {
        Success = true,
        Message = AcceptedMessage,
        SalesOrderId = order.Id,
        SalesOrderNumber = order.OrderNumber,
        ExternalReference = externalReference,
        ReservationId = reservationId,
        QueueId = queueId,
        Status = status,
        EstimatedProcessingSeconds = 15
    };
}
