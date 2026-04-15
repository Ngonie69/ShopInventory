using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Products.Queries.GetPagedProductsInWarehouse;

public sealed record GetPagedProductsInWarehouseQuery(
    string WarehouseCode,
    int Page = 1,
    int PageSize = 20
) : IRequest<ErrorOr<WarehouseProductsPagedResponseDto>>;
