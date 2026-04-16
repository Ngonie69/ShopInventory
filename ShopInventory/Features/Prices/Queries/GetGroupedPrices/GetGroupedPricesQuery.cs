using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetGroupedPrices;

public sealed record GetGroupedPricesQuery() : IRequest<ErrorOr<ItemPricesGroupedResponseDto>>;
