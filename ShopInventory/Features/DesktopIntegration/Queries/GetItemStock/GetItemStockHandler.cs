using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetItemStock;

public sealed class GetItemStockHandler(
    IStockReservationService reservationService
) : IRequestHandler<GetItemStockQuery, ErrorOr<ReservedStockSummaryDto>>
{
    public async Task<ErrorOr<ReservedStockSummaryDto>> Handle(
        GetItemStockQuery query,
        CancellationToken cancellationToken)
    {
        var summary = await reservationService.GetReservedStockSummaryAsync(
            query.ItemCode, query.WarehouseCode, cancellationToken);

        return summary;
    }
}
