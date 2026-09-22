using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
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
/// </remarks>
public sealed class ConvertSalesOrderToInvoiceHandler(
    ISalesOrderService salesOrderService,
    IStockReservationService reservationService,
    IInvoiceQueueService queueService,
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<ConvertSalesOrderToInvoiceHandler> logger
) : IRequestHandler<ConvertSalesOrderToInvoiceCommand, ErrorOr<ConvertSalesOrderToInvoiceResponseDto>>
{
    internal const string IdempotencyScope = "sales-order-invoice-conversion";

    private const string AcceptedMessage =
        "Sales order converted to invoice and queued for SAP posting. Poll the status endpoint to check completion.";

    public async Task<ErrorOr<ConvertSalesOrderToInvoiceResponseDto>> Handle(
        ConvertSalesOrderToInvoiceCommand command,
        CancellationToken cancellationToken)
    {
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
                    cancellationToken);

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

            var order = await salesOrderService.GetByIdFromLocalAsync(request.SalesOrderId, cancellationToken);

            if (order == null)
                return Errors.DesktopIntegration.ValidationFailed(
                    $"Sales order with ID {request.SalesOrderId} not found");

            // Before the status check: the first conversion is what moved the order out of Approved,
            // so a retry that outlived the claim would otherwise be refused for having succeeded.
            if (suppliedReference is not null)
            {
                var queued = await queueService.GetQueueStatusAsync(suppliedReference, cancellationToken);
                if (queued is not null)
                {
                    if (queued.SalesOrderId != order.Id)
                        return Errors.DesktopIntegration.ValidationFailed(
                            $"Reference '{suppliedReference}' has already been used for a different invoice");

                    logger.LogInformation(
                        "Sales order {OrderNumber} was already converted under {ExternalRef} (QueueId={QueueId}); replaying",
                        order.OrderNumber, suppliedReference, queued.QueueId);

                    var replay = Accepted(order, suppliedReference, queued.ReservationId, queued.QueueId, queued.Status);
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
                reservationRequest, command.CreatedBy, cancellationToken);

            if (!reservationResult.Success)
            {
                logger.LogWarning(
                    "Stock reservation failed for sales order {OrderNumber} conversion: {Errors}",
                    order.OrderNumber,
                    string.Join("; ", reservationResult.Errors?.Select(e => e.Message) ?? Array.Empty<string>()));

                return Errors.DesktopIntegration.ReservationFailed(
                    "Stock reservation failed — insufficient stock or batch allocation error");
            }

            var reservationId = reservationResult.Reservation!.ReservationId;

            // Queue the invoice for batch posting to SAP
            var queueResult = await queueService.EnqueueInvoiceAsync(
                reservationRequest,
                reservationId,
                command.CreatedBy,
                cancellationToken,
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
                }, cancellationToken);

                return Errors.DesktopIntegration.InvoiceCreationFailed(
                    queueResult.ErrorMessage ?? "Failed to queue invoice for SAP posting");
            }

            // Mark the sales order as fulfilled
            try
            {
                await salesOrderService.MarkAsFulfilledAsync(order.Id, null, cancellationToken);
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
