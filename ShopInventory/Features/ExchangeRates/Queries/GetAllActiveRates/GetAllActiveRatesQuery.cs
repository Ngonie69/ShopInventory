using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExchangeRates.Queries.GetAllActiveRates;

public sealed record GetAllActiveRatesQuery() : IRequest<ErrorOr<List<ExchangeRateDto>>>;
