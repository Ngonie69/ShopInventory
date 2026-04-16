using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.ListReservations;

public sealed class ListReservationsHandler(
    IStockReservationService reservationService
) : IRequestHandler<ListReservationsQuery, ErrorOr<ReservationListResponseDto>>
{
    public async Task<ErrorOr<ReservationListResponseDto>> Handle(
        ListReservationsQuery query,
        CancellationToken cancellationToken)
    {
        var queryParams = new ReservationQueryParams
        {
            SourceSystem = query.SourceSystem,
            Status = query.Status,
            CardCode = query.CardCode,
            ExternalReferenceId = query.ExternalReferenceId,
            ActiveOnly = query.ActiveOnly,
            Page = query.Page,
            PageSize = Math.Min(query.PageSize, 100)
        };

        var result = await reservationService.ListReservationsAsync(queryParams, cancellationToken);
        return result;
    }
}
