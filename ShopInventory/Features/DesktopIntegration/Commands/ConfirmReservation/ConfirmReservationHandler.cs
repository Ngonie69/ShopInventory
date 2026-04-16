using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConfirmReservation;

public sealed class ConfirmReservationHandler(
    IStockReservationService reservationService,
    ILogger<ConfirmReservationHandler> logger
) : IRequestHandler<ConfirmReservationCommand, ErrorOr<ConfirmReservationResponseDto>>
{
    public async Task<ErrorOr<ConfirmReservationResponseDto>> Handle(
        ConfirmReservationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Desktop app confirming reservation: {ReservationId}", command.Request.ReservationId);

            var result = await reservationService.ConfirmReservationAsync(command.Request, cancellationToken);

            if (!result.Success)
            {
                if (result.Errors.Any(e => e.Contains("not found", StringComparison.OrdinalIgnoreCase)))
                    return Errors.DesktopIntegration.ReservationNotFound(command.Request.ReservationId);

                return Errors.DesktopIntegration.ConfirmationFailed(result.Message ?? "Confirmation failed");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error confirming reservation {ReservationId}", command.Request.ReservationId);
            return Errors.DesktopIntegration.ConfirmationFailed(ex.Message);
        }
    }
}
