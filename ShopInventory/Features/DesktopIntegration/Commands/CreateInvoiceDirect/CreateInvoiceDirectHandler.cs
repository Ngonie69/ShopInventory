using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateInvoiceDirect;

public sealed class CreateInvoiceDirectHandler(
    IStockReservationService reservationService,
    ILogger<CreateInvoiceDirectHandler> logger
) : IRequestHandler<CreateInvoiceDirectCommand, ErrorOr<ConfirmReservationResponseDto>>
{
    public async Task<ErrorOr<ConfirmReservationResponseDto>> Handle(
        CreateInvoiceDirectCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = command.Request;
            var externalRef = request.ExternalReferenceId ??
                $"DESKTOP-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";

            logger.LogInformation("Desktop app creating direct invoice: {ExternalRef}", externalRef);

            // Step 1: Create a reservation
            var reservationRequest = new CreateStockReservationRequest
            {
                ExternalReferenceId = externalRef,
                SourceSystem = request.SourceSystem ?? "DESKTOP_APP",
                DocumentType = ReservationDocumentType.Invoice,
                CardCode = request.CardCode,
                CardName = request.CardName,
                Currency = request.DocCurrency,
                ReservationDurationMinutes = 5,
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

            // Step 2: Immediately confirm the reservation
            var confirmRequest = new ConfirmReservationRequest
            {
                ReservationId = reservationResult.Reservation!.ReservationId,
                DocDate = request.DocDate,
                DocDueDate = request.DocDueDate,
                NumAtCard = request.NumAtCard,
                Comments = request.Comments,
                SalesPersonCode = request.SalesPersonCode,
                Fiscalize = request.Fiscalize
            };

            var confirmResult = await reservationService.ConfirmReservationAsync(confirmRequest, cancellationToken);

            if (!confirmResult.Success)
            {
                // Cancel the reservation if SAP posting failed
                await reservationService.CancelReservationAsync(new CancelReservationRequest
                {
                    ReservationId = reservationResult.Reservation.ReservationId,
                    Reason = $"SAP posting failed: {string.Join(", ", confirmResult.Errors)}"
                }, cancellationToken);

                return Errors.DesktopIntegration.InvoiceCreationFailed(
                    confirmResult.Message ?? "Invoice creation failed");
            }

            return confirmResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating direct invoice");
            return Errors.DesktopIntegration.InvoiceCreationFailed(ex.Message);
        }
    }
}
