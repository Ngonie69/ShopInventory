using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.RenewReservation;

public sealed class RenewReservationHandler(
    IStockReservationService reservationService,
    ILogger<RenewReservationHandler> logger
) : IRequestHandler<RenewReservationCommand, ErrorOr<StockReservationResponseDto>>
{
    public async Task<ErrorOr<StockReservationResponseDto>> Handle(
        RenewReservationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Desktop app renewing reservation: {ReservationId} for {Minutes} minutes",
                command.Request.ReservationId, command.Request.ExtensionMinutes);

            var result = await reservationService.RenewReservationAsync(command.Request, cancellationToken);

            if (!result.Success)
            {
                if (result.Errors.Any(e => e.ErrorCode == ReservationErrorCode.ReservationNotFound))
                    return Errors.DesktopIntegration.ReservationNotFound(command.Request.ReservationId);

                return Errors.DesktopIntegration.ReservationFailed(result.Message ?? "Renewal failed");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error renewing reservation {ReservationId}", command.Request.ReservationId);
            return Errors.DesktopIntegration.ReservationFailed(ex.Message);
        }
    }
}
