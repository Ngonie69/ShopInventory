using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Stock.Queries.GetStockInWarehouse;

public sealed record GetStockInWarehouseQuery(
    string WarehouseCode,
    bool IncludePackagingStock = true
) : IRequest<ErrorOr<WarehouseStockResponseDto>>;
