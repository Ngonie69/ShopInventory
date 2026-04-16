using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetPricesByCurrency;

public sealed record GetPricesByCurrencyQuery(string Currency) : IRequest<ErrorOr<ItemPricesResponseDto>>;
