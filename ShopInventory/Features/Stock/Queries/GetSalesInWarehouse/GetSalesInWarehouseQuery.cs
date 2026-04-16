using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Stock.Queries.GetSalesInWarehouse;

public sealed record GetSalesInWarehouseQuery(
    string WarehouseCode,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<WarehouseSalesResponseDto>>;
