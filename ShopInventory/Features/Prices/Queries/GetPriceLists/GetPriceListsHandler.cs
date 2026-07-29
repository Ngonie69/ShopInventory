using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPriceLists;

public sealed class GetPriceListsHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ILogger<GetPriceListsHandler> logger
) : IRequestHandler<GetPriceListsQuery, ErrorOr<PriceListsResponseDto>>
{
    public async Task<ErrorOr<PriceListsResponseDto>> Handle(
        GetPriceListsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await localPriceCatalogService.GetPriceListsAsync(cancellationToken);

            logger.LogInformation("Retrieved {Count} locally stored price lists", response.TotalCount);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving locally stored price lists");
            return Errors.Price.SapError(ex.Message);
        }
    }
}
