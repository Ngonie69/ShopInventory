using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.ExchangeRates.Queries.ConvertCurrency;

public sealed class ConvertCurrencyHandler(
    IExchangeRateService exchangeRateService,
    ILogger<ConvertCurrencyHandler> logger
) : IRequestHandler<ConvertCurrencyQuery, ErrorOr<ConvertResponse>>
{
    public async Task<ErrorOr<ConvertResponse>> Handle(
        ConvertCurrencyQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var converted = await exchangeRateService.ConvertAsync(
                request.Amount, request.FromCurrency, request.ToCurrency, cancellationToken);

            return new ConvertResponse
            {
                Amount = request.Amount,
                FromCurrency = request.FromCurrency,
                ToCurrency = request.ToCurrency,
                ConvertedAmount = converted,
                Rate = request.Amount > 0 ? converted / request.Amount : 0
            };
        }
        catch (InvalidOperationException ex)
        {
            return Errors.ExchangeRate.ConversionFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error converting {Amount} {From} to {To}", request.Amount, request.FromCurrency, request.ToCurrency);
            return Errors.ExchangeRate.FetchFailed(ex.Message);
        }
    }
}
