using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExchangeRates.Queries.ConvertCurrency;

public sealed record ConvertCurrencyQuery(decimal Amount, string FromCurrency, string ToCurrency) : IRequest<ErrorOr<ConvertResponse>>;
