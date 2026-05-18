using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetGroupedPrices;

public sealed class GetGroupedPricesHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ILogger<GetGroupedPricesHandler> logger
) : IRequestHandler<GetGroupedPricesQuery, ErrorOr<ItemPricesGroupedResponseDto>>
{
    public async Task<ErrorOr<ItemPricesGroupedResponseDto>> Handle(
        GetGroupedPricesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await localPriceCatalogService.GetGroupedPricesAsync(cancellationToken);

            logger.LogInformation("Retrieved {Count} items with locally stored grouped prices", response.TotalItems);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving locally stored grouped item prices");
            return Errors.Price.SapError(ex.Message);
        }
    }
}
