using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Stock.Queries.GetStockForItemsInWarehouse;

public sealed record GetStockForItemsInWarehouseQuery(
    string WarehouseCode,
    IReadOnlyCollection<string> ItemCodes
) : IRequest<ErrorOr<WarehouseStockResponseDto>>;