using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExchangeRates.Queries.GetRateHistory;

public sealed record GetRateHistoryQuery(string FromCurrency, string ToCurrency, int Days = 30) : IRequest<ErrorOr<ExchangeRateHistoryDto>>;
