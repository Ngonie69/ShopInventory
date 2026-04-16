using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetPriceByItemCode;

public sealed record GetPriceByItemCodeQuery(string ItemCode) : IRequest<ErrorOr<ItemPriceGroupedDto>>;
