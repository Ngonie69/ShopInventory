using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Stock.Queries.GetWarehouses;

public sealed record GetWarehousesQuery() : IRequest<ErrorOr<WarehouseListResponseDto>>;
