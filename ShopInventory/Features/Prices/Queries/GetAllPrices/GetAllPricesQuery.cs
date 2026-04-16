using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetAllPrices;

public sealed record GetAllPricesQuery() : IRequest<ErrorOr<ItemPricesResponseDto>>;
