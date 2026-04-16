using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetReservedStockSummary;

public sealed class GetReservedStockSummaryHandler(
    IStockReservationService reservationService,
    ISAPServiceLayerClient sapClient
) : IRequestHandler<GetReservedStockSummaryQuery, ErrorOr<List<ReservedStockSummaryDto>>>
{
    public async Task<ErrorOr<List<ReservedStockSummaryDto>>> Handle(
        GetReservedStockSummaryQuery query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query.ItemCodes))
        {
            // Return all stock in warehouse
            var stockItems = await sapClient.GetStockQuantitiesInWarehouseAsync(
                query.WarehouseCode, cancellationToken);
            var summaries = new List<ReservedStockSummaryDto>();

            foreach (var item in stockItems.Take(100))
            {
                var summary = await reservationService.GetReservedStockSummaryAsync(
                    item.ItemCode ?? "", query.WarehouseCode, cancellationToken);
                summaries.Add(summary);
            }

            return summaries;
        }

        var codes = query.ItemCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<ReservedStockSummaryDto>();

        foreach (var code in codes)
        {
            var summary = await reservationService.GetReservedStockSummaryAsync(
                code, query.WarehouseCode, cancellationToken);
            results.Add(summary);
        }

        return results;
    }
}
