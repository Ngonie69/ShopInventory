using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetPriceLists;

public sealed record GetPriceListsQuery(bool ForceRefresh) : IRequest<ErrorOr<PriceListsResponseDto>>;
