using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.ExchangeRates.Queries.GetCurrentRate;

public sealed class GetCurrentRateHandler(
    IExchangeRateService exchangeRateService,
    ILogger<GetCurrentRateHandler> logger
) : IRequestHandler<GetCurrentRateQuery, ErrorOr<ExchangeRateDto>>
{
    public async Task<ErrorOr<ExchangeRateDto>> Handle(
        GetCurrentRateQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var rate = await exchangeRateService.GetCurrentRateAsync(request.FromCurrency, request.ToCurrency, cancellationToken);

            if (rate is null)
                return Errors.ExchangeRate.NotFound(request.FromCurrency, request.ToCurrency);

            return rate;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching exchange rate {FromCurrency}/{ToCurrency}", request.FromCurrency, request.ToCurrency);
            return Errors.ExchangeRate.FetchFailed(ex.Message);
        }
    }
}
