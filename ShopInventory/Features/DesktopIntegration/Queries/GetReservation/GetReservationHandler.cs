using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetReservation;

public sealed class GetReservationHandler(
    IStockReservationService reservationService
) : IRequestHandler<GetReservationQuery, ErrorOr<StockReservationDto>>
{
    public async Task<ErrorOr<StockReservationDto>> Handle(
        GetReservationQuery query,
        CancellationToken cancellationToken)
    {
        var reservation = await reservationService.GetReservationAsync(query.ReservationId, cancellationToken);

        if (reservation == null)
            return Errors.DesktopIntegration.ReservationNotFound(query.ReservationId);

        return reservation;
    }
}
