using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPricesByCurrency;

public sealed class GetPricesByCurrencyHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ILogger<GetPricesByCurrencyHandler> logger
) : IRequestHandler<GetPricesByCurrencyQuery, ErrorOr<ItemPricesResponseDto>>
{
    public async Task<ErrorOr<ItemPricesResponseDto>> Handle(
        GetPricesByCurrencyQuery request,
        CancellationToken cancellationToken)
    {
        var normalizedCurrency = request.Currency.ToUpperInvariant();
        if (normalizedCurrency is not ("USD" or "ZIG"))
            return Errors.Price.InvalidCurrency(request.Currency);

        try
        {
            var response = await localPriceCatalogService.GetPricesByCurrencyAsync(normalizedCurrency, cancellationToken);

            logger.LogInformation("Retrieved {Count} locally stored item prices for currency {Currency}",
                response.TotalCount, normalizedCurrency);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving locally stored item prices for currency {Currency}", request.Currency);
            return Errors.Price.SapError(ex.Message);
        }
    }
}
