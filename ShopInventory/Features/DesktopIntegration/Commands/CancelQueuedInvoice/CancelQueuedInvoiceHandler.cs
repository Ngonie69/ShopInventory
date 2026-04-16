using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CancelQueuedInvoice;

public sealed class CancelQueuedInvoiceHandler(
    IInvoiceQueueService queueService,
    IStockReservationService reservationService,
    ILogger<CancelQueuedInvoiceHandler> logger
) : IRequestHandler<CancelQueuedInvoiceCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        CancelQueuedInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await queueService.GetQueueStatusAsync(command.ExternalReference, cancellationToken);

            if (status == null)
                return Errors.DesktopIntegration.QueueNotFound(command.ExternalReference);

            if (!status.CanCancel)
                return Errors.DesktopIntegration.CancellationFailed(
                    $"Invoice cannot be cancelled - current status: {status.Status}");

            var success = await queueService.CancelQueuedInvoiceAsync(
                command.ExternalReference, command.CancelledBy, cancellationToken);

            if (!success)
                return Errors.DesktopIntegration.CancellationFailed("Failed to cancel queued invoice");

            // Also cancel the reservation
            await reservationService.CancelReservationAsync(new CancelReservationRequest
            {
                ReservationId = status.ReservationId,
                Reason = "Queued invoice cancelled"
            }, cancellationToken);

            return Result.Deleted;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cancelling queued invoice {ExternalReference}", command.ExternalReference);
            return Errors.DesktopIntegration.CancellationFailed(ex.Message);
        }
    }
}
