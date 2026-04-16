using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;

public sealed class ConvertSalesOrderToInvoiceHandler(
    ISalesOrderService salesOrderService,
    IStockReservationService reservationService,
    IInvoiceQueueService queueService,
    ILogger<ConvertSalesOrderToInvoiceHandler> logger
) : IRequestHandler<ConvertSalesOrderToInvoiceCommand, ErrorOr<ConvertSalesOrderToInvoiceResponseDto>>
{
    public async Task<ErrorOr<ConvertSalesOrderToInvoiceResponseDto>> Handle(
        ConvertSalesOrderToInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = command.Request;

            var order = await salesOrderService.GetByIdFromLocalAsync(request.SalesOrderId, cancellationToken);

            if (order == null)
                return Errors.DesktopIntegration.ValidationFailed(
                    $"Sales order with ID {request.SalesOrderId} not found");

            if (order.Status != SalesOrderStatus.Approved)
                return Errors.DesktopIntegration.ValidationFailed(
                    $"Only approved orders can be converted to invoices. Current status: {order.StatusName}");

            if (!order.Lines.Any())
                return Errors.DesktopIntegration.ValidationFailed("Sales order has no line items");

            var externalRef = request.ExternalReferenceId ??
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

            // Queue the invoice for batch posting to SAP
            var queueResult = await queueService.EnqueueInvoiceAsync(
                reservationRequest,
                reservationResult.Reservation!.ReservationId,
                command.CreatedBy,
                cancellationToken);

            if (!queueResult.Success)
            {
                await reservationService.CancelReservationAsync(new CancelReservationRequest
                {
                    ReservationId = reservationResult.Reservation.ReservationId,
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
                order.OrderNumber, externalRef,
                reservationResult.Reservation.ReservationId, queueResult.QueueId);

            return new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = true,
                Message = "Sales order converted to invoice and queued for SAP posting. Poll the status endpoint to check completion.",
                SalesOrderId = order.Id,
                SalesOrderNumber = order.OrderNumber,
                ExternalReference = externalRef,
                ReservationId = reservationResult.Reservation.ReservationId,
                QueueId = queueResult.QueueId,
                Status = "Pending",
                EstimatedProcessingSeconds = 15
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error converting sales order to invoice");
            return Errors.DesktopIntegration.InvoiceCreationFailed(ex.Message);
        }
    }
}
