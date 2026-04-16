using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Stock.Queries.GetSalesInWarehousePost;

public sealed record GetSalesInWarehousePostQuery(
    string WarehouseCode,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<WarehouseSalesResponseDto>>;
