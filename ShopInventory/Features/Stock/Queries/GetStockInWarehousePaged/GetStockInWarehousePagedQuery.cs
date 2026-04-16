using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Stock.Queries.GetStockInWarehousePaged;

public sealed record GetStockInWarehousePagedQuery(
    string WarehouseCode,
    int Page = 1,
    int PageSize = 50
) : IRequest<ErrorOr<WarehouseStockPagedResponseDto>>;
