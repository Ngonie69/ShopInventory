using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetGroupedPrices;

public sealed class GetGroupedPricesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetGroupedPricesHandler> logger
) : IRequestHandler<GetGroupedPricesQuery, ErrorOr<ItemPricesGroupedResponseDto>>
{
    public async Task<ErrorOr<ItemPricesGroupedResponseDto>> Handle(
        GetGroupedPricesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Price.SapDisabled;

        try
        {
#pragma warning disable CS0618 // Legacy endpoint — migration to price-list API requires SAP config
            var prices = await sapClient.GetItemPricesAsync(cancellationToken);
#pragma warning restore CS0618

            var groupedItems = prices
                .GroupBy(p => p.ItemCode)
                .Select(g => new ItemPriceGroupedDto
                {
                    ItemCode = g.Key,
                    ItemName = g.First().ItemName,
                    UsdPrice = g.FirstOrDefault(p => p.Currency == "USD")?.Price,
                    ZigPrice = g.FirstOrDefault(p => p.Currency == "ZIG")?.Price
                })
                .OrderBy(i => i.ItemCode)
                .ToList();

            var response = new ItemPricesGroupedResponseDto
            {
                TotalItems = groupedItems.Count,
                Items = groupedItems
            };

            logger.LogInformation("Retrieved {Count} items with grouped prices", response.TotalItems);

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
            logger.LogError(ex, "Error retrieving grouped item prices");
            return Errors.Price.SapError(ex.Message);
        }
    }
}
