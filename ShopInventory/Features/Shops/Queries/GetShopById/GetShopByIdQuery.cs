using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Queries.GetShopById;

public sealed record GetShopByIdQuery(int ShopId) : IRequest<ErrorOr<ShopDto>>;
