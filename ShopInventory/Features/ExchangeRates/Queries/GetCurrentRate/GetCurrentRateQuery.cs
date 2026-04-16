using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExchangeRates.Queries.GetCurrentRate;

public sealed record GetCurrentRateQuery(string FromCurrency, string ToCurrency) : IRequest<ErrorOr<ExchangeRateDto>>;
