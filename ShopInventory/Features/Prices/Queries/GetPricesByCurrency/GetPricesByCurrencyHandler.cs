using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPricesByCurrency;

public sealed class GetPricesByCurrencyHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPricesByCurrencyHandler> logger
) : IRequestHandler<GetPricesByCurrencyQuery, ErrorOr<ItemPricesResponseDto>>
{
    public async Task<ErrorOr<ItemPricesResponseDto>> Handle(
        GetPricesByCurrencyQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        var normalizedCurrency = request.Currency.ToUpperInvariant();
        if (normalizedCurrency is not ("USD" or "ZIG"))
            return Errors.Price.InvalidCurrency(request.Currency);

        try
        {
            var allPrices = await sapClient.GetItemPricesAsync(cancellationToken);
            var filteredPrices = allPrices.Where(p => p.Currency == normalizedCurrency).ToList();

            var response = new ItemPricesResponseDto
            {
                TotalCount = filteredPrices.Count,
                UsdPriceCount = normalizedCurrency == "USD" ? filteredPrices.Count : 0,
                ZigPriceCount = normalizedCurrency == "ZIG" ? filteredPrices.Count : 0,
                Prices = filteredPrices
            };

            logger.LogInformation("Retrieved {Count} item prices for currency {Currency}",
                response.TotalCount, normalizedCurrency);

            return response;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Price.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Price.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving item prices for currency {Currency}", request.Currency);
            return Errors.Price.SapError(ex.Message);
        }
    }
}
