using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.ExchangeRates.Queries.GetAllActiveRates;

public sealed class GetAllActiveRatesHandler(
    IExchangeRateService exchangeRateService,
    ILogger<GetAllActiveRatesHandler> logger
) : IRequestHandler<GetAllActiveRatesQuery, ErrorOr<List<ExchangeRateDto>>>
{
    public async Task<ErrorOr<List<ExchangeRateDto>>> Handle(
        GetAllActiveRatesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var rates = await exchangeRateService.GetAllActiveRatesAsync(cancellationToken);
            return rates;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching exchange rates");
            return Errors.ExchangeRate.FetchFailed(ex.Message);
        }
    }
}
