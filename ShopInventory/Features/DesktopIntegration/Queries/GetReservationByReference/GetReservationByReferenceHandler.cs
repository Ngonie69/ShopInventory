using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetReservationByReference;

public sealed class GetReservationByReferenceHandler(
    IStockReservationService reservationService
) : IRequestHandler<GetReservationByReferenceQuery, ErrorOr<StockReservationDto>>
{
    public async Task<ErrorOr<StockReservationDto>> Handle(
        GetReservationByReferenceQuery query,
        CancellationToken cancellationToken)
    {
        var reservation = await reservationService.GetReservationByExternalReferenceAsync(
            query.ExternalReferenceId, cancellationToken);

        if (reservation == null)
            return Errors.DesktopIntegration.ReservationNotFound(query.ExternalReferenceId);

        return reservation;
    }
}
