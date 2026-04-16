using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.ExchangeRates.Queries.GetRateHistory;

public sealed class GetRateHistoryHandler(
    IExchangeRateService exchangeRateService,
    ILogger<GetRateHistoryHandler> logger
) : IRequestHandler<GetRateHistoryQuery, ErrorOr<ExchangeRateHistoryDto>>
{
    public async Task<ErrorOr<ExchangeRateHistoryDto>> Handle(
        GetRateHistoryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var history = await exchangeRateService.GetRateHistoryAsync(
                request.FromCurrency, request.ToCurrency, request.Days, cancellationToken);

            return history;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching exchange rate history {FromCurrency}/{ToCurrency}", request.FromCurrency, request.ToCurrency);
            return Errors.ExchangeRate.FetchFailed(ex.Message);
        }
    }
}
