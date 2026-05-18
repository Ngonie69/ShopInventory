using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPricesByPriceList;

public sealed class GetPricesByPriceListHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ILogger<GetPricesByPriceListHandler> logger
) : IRequestHandler<GetPricesByPriceListQuery, ErrorOr<ItemPricesByListResponseDto>>
{
    public async Task<ErrorOr<ItemPricesByListResponseDto>> Handle(
        GetPricesByPriceListQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await localPriceCatalogService.GetPricesByPriceListAsync(
                request.PriceListNum,
                cancellationToken: cancellationToken);

            logger.LogInformation("Retrieved {Count} locally stored item prices for price list {PriceListNum} ({PriceListName})",
                response.TotalCount, request.PriceListNum, response.PriceListName);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving locally stored prices from price list {PriceListNum}", request.PriceListNum);
            return Errors.Price.SapError(ex.Message);
        }
    }
}
