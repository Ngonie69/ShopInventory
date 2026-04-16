using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPriceByItemCode;

public sealed class GetPriceByItemCodeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPriceByItemCodeHandler> logger
) : IRequestHandler<GetPriceByItemCodeQuery, ErrorOr<ItemPriceGroupedDto>>
{
    public async Task<ErrorOr<ItemPriceGroupedDto>> Handle(
        GetPriceByItemCodeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        try
        {
            var prices = await sapClient.GetItemPriceByCodeAsync(request.ItemCode, cancellationToken);

            if (prices.Count == 0)
                return Errors.Price.NotFound(request.ItemCode);

            var response = new ItemPriceGroupedDto
            {
                ItemCode = request.ItemCode,
                ItemName = prices.First().ItemName,
                UsdPrice = prices.FirstOrDefault(p => p.Currency == "USD")?.Price,
                ZigPrice = prices.FirstOrDefault(p => p.Currency == "ZIG")?.Price
            };

            logger.LogInformation("Retrieved prices for item {ItemCode}: USD={UsdPrice}, ZIG={ZigPrice}",
                request.ItemCode, response.UsdPrice, response.ZigPrice);

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
            logger.LogError(ex, "Error retrieving prices for item {ItemCode}", request.ItemCode);
            return Errors.Price.SapError(ex.Message);
        }
    }
}
