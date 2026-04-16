using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateReservation;

public sealed class CreateReservationHandler(
    IStockReservationService reservationService,
    ILogger<CreateReservationHandler> logger
) : IRequestHandler<CreateReservationCommand, ErrorOr<StockReservationResponseDto>>
{
    public async Task<ErrorOr<StockReservationResponseDto>> Handle(
        CreateReservationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "Desktop app creating reservation: ExternalRef={ExternalRef}, Source={Source}, Lines={LineCount}",
                command.Request.ExternalReferenceId, command.Request.SourceSystem, command.Request.Lines.Count);

            var result = await reservationService.CreateReservationAsync(
                command.Request, command.CreatedBy, cancellationToken);

            if (!result.Success)
            {
                if (result.Errors.Any(e => e.ErrorCode == ReservationErrorCode.DuplicateReference))
                    return Errors.DesktopIntegration.ReservationFailed(result.Message ?? "Duplicate reference");

                return Errors.DesktopIntegration.ReservationFailed(result.Message ?? "Reservation failed");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating reservation");
            return Errors.DesktopIntegration.ReservationFailed(ex.Message);
        }
    }
}
