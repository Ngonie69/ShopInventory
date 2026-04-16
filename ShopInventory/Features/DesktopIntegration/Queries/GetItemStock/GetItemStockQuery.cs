using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetItemStock;

public sealed record GetItemStockQuery(
    string WarehouseCode,
    string ItemCode
) : IRequest<ErrorOr<ReservedStockSummaryDto>>;
