using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetAllPrices;

public sealed class GetAllPricesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetAllPricesHandler> logger
) : IRequestHandler<GetAllPricesQuery, ErrorOr<ItemPricesResponseDto>>
{
    public async Task<ErrorOr<ItemPricesResponseDto>> Handle(
        GetAllPricesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        try
        {
#pragma warning disable CS0618 // Legacy endpoint — migration to price-list API requires SAP config
            var prices = await sapClient.GetItemPricesAsync(cancellationToken);
#pragma warning restore CS0618

            var response = new ItemPricesResponseDto
            {
                TotalCount = prices.Count,
                UsdPriceCount = prices.Count(p => p.Currency == "USD"),
                ZigPriceCount = prices.Count(p => p.Currency == "ZIG"),
                Prices = prices
            };

            logger.LogInformation("Retrieved {Count} item prices ({UsdCount} USD, {ZigCount} ZIG)",
                response.TotalCount, response.UsdPriceCount, response.ZigPriceCount);

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
            logger.LogError(ex, "Error retrieving item prices");
            return Errors.Price.SapError(ex.Message);
        }
    }
}
