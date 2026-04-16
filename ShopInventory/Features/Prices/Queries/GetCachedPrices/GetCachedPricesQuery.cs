using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetCachedPrices;

public sealed record GetCachedPricesQuery() : IRequest<ErrorOr<object>>;
