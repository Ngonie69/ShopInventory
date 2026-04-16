using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CancelReservation;

public sealed class CancelReservationHandler(
    IStockReservationService reservationService,
    ILogger<CancelReservationHandler> logger
) : IRequestHandler<CancelReservationCommand, ErrorOr<StockReservationResponseDto>>
{
    public async Task<ErrorOr<StockReservationResponseDto>> Handle(
        CancelReservationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Desktop app cancelling reservation: {ReservationId}", command.Request.ReservationId);

            var result = await reservationService.CancelReservationAsync(command.Request, cancellationToken);

            if (!result.Success)
            {
                if (result.Errors.Any(e => e.ErrorCode == ReservationErrorCode.ReservationNotFound))
                    return Errors.DesktopIntegration.ReservationNotFound(command.Request.ReservationId);

                return Errors.DesktopIntegration.CancellationFailed(result.Message ?? "Cancellation failed");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cancelling reservation {ReservationId}", command.Request.ReservationId);
            return Errors.DesktopIntegration.CancellationFailed(ex.Message);
        }
    }
}
