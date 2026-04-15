using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Products.Queries.GetProductsInWarehouse;

public sealed record GetProductsInWarehouseQuery(string WarehouseCode) : IRequest<ErrorOr<WarehouseProductsResponseDto>>;
