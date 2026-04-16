using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateQueuedInvoice;

public sealed class CreateQueuedInvoiceHandler(
    IStockReservationService reservationService,
    IInvoiceQueueService queueService,
    ILogger<CreateQueuedInvoiceHandler> logger
) : IRequestHandler<CreateQueuedInvoiceCommand, ErrorOr<QueuedInvoiceResponseDto>>
{
    public async Task<ErrorOr<QueuedInvoiceResponseDto>> Handle(
        CreateQueuedInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = command.Request;
            var externalRef = request.ExternalReferenceId ??
                $"DESKTOP-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";

            logger.LogInformation("Desktop app creating queued invoice: {ExternalRef}", externalRef);

            // Step 1: Create a reservation to hold the stock
            var reservationRequest = new CreateStockReservationRequest
            {
                ExternalReference = externalRef,
                ExternalReferenceId = externalRef,
                SourceSystem = request.SourceSystem ?? "DESKTOP_APP",
                DocumentType = ReservationDocumentType.Invoice,
                CardCode = request.CardCode,
                CardName = request.CardName,
                Currency = request.DocCurrency,
                ReservationDurationMinutes = 60,
                RequiresFiscalization = request.Fiscalize,
                Notes = request.Comments,
                Lines = request.Lines.Select(l => new CreateStockReservationLineRequest
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
                return Errors.DesktopIntegration.ReservationFailed(reservationResult.Message ?? "Reservation failed");

            // Step 2: Queue the invoice for batch posting
            var queueResult = await queueService.EnqueueInvoiceAsync(
                reservationRequest,
                reservationResult.Reservation!.ReservationId,
                command.CreatedBy,
                cancellationToken);

            if (!queueResult.Success)
            {
                // If queuing fails, cancel the reservation
                await reservationService.CancelReservationAsync(new CancelReservationRequest
                {
                    ReservationId = reservationResult.Reservation.ReservationId,
                    Reason = $"Failed to queue invoice: {queueResult.ErrorMessage}"
                }, cancellationToken);

                return Errors.DesktopIntegration.InvoiceCreationFailed(
                    queueResult.ErrorMessage ?? "Failed to queue invoice");
            }

            logger.LogInformation(
                "Invoice queued successfully: ExternalRef={ExternalRef}, ReservationId={ReservationId}, QueueId={QueueId}",
                externalRef, reservationResult.Reservation.ReservationId, queueResult.QueueId);

            return new QueuedInvoiceResponseDto
            {
                Success = true,
                Message = "Invoice queued for processing. Poll the status endpoint to check completion.",
                ExternalReference = externalRef,
                ReservationId = reservationResult.Reservation.ReservationId,
                QueueId = queueResult.QueueId,
                Status = "Pending",
                EstimatedProcessingSeconds = 15
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating queued invoice");
            return Errors.DesktopIntegration.InvoiceCreationFailed(ex.Message);
        }
    }
}
