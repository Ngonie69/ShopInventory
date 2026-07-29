using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPriceByItemCode;

public sealed class GetPriceByItemCodeHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ILogger<GetPriceByItemCodeHandler> logger
) : IRequestHandler<GetPriceByItemCodeQuery, ErrorOr<ItemPriceGroupedDto>>
{
    public async Task<ErrorOr<ItemPriceGroupedDto>> Handle(
        GetPriceByItemCodeQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await localPriceCatalogService.GetGroupedPriceByItemCodeAsync(request.ItemCode, cancellationToken);

            if (response is null)
                return Errors.Price.NotFound(request.ItemCode);

            logger.LogInformation("Retrieved locally stored prices for item {ItemCode}: USD={UsdPrice}, ZIG={ZigPrice}",
                request.ItemCode, response.UsdPrice, response.ZigPrice);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving locally stored prices for item {ItemCode}", request.ItemCode);
            return Errors.Price.SapError(ex.Message);
        }
    }
}
