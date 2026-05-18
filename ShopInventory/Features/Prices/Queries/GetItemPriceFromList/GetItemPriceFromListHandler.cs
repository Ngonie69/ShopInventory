using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetItemPriceFromList;

public sealed class GetItemPriceFromListHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ILogger<GetItemPriceFromListHandler> logger) : IRequestHandler<GetItemPriceFromListQuery, ErrorOr<ItemPriceByListDto>>
{
    public async Task<ErrorOr<ItemPriceByListDto>> Handle(
        GetItemPriceFromListQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var price = await localPriceCatalogService.GetItemPriceFromListAsync(
                request.PriceListNum,
                request.ItemCode,
                cancellationToken);

            if (price is null)
            {
                return Errors.Price.NotFound(request.ItemCode);
            }

            logger.LogInformation(
                "Retrieved locally stored price for item {ItemCode} from price list {PriceListNum}",
                request.ItemCode,
                request.PriceListNum);

            return price;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error retrieving locally stored price for item {ItemCode} from price list {PriceListNum}",
                request.ItemCode,
                request.PriceListNum);
            return Errors.Price.SapError(ex.Message);
        }
    }
}