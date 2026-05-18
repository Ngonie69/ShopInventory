using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetAllPrices;

public sealed class GetAllPricesHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ILogger<GetAllPricesHandler> logger
) : IRequestHandler<GetAllPricesQuery, ErrorOr<ItemPricesResponseDto>>
{
    public async Task<ErrorOr<ItemPricesResponseDto>> Handle(
        GetAllPricesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await localPriceCatalogService.GetAllPricesAsync(cancellationToken);

            logger.LogInformation("Retrieved {Count} locally stored item prices ({UsdCount} USD, {ZigCount} ZIG)",
                response.TotalCount, response.UsdPriceCount, response.ZigPriceCount);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving locally stored item prices");
            return Errors.Price.SapError(ex.Message);
        }
    }
}
