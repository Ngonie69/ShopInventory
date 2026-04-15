using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Products.Queries.GetProductBatches;

public sealed record GetProductBatchesQuery(
    string WarehouseCode,
    string ItemCode
) : IRequest<ErrorOr<ProductBatchesResponseDto>>;
