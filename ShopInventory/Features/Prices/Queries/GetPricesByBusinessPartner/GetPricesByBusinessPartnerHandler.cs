using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPricesByBusinessPartner;

public sealed class GetPricesByBusinessPartnerHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ILogger<GetPricesByBusinessPartnerHandler> logger)
    : IRequestHandler<GetPricesByBusinessPartnerQuery, ErrorOr<ItemPricesByListResponseDto>>
{
    public async Task<ErrorOr<ItemPricesByListResponseDto>> Handle(
        GetPricesByBusinessPartnerQuery request, CancellationToken cancellationToken)
    {
        var pricing = await localPriceCatalogService.GetBusinessPartnerPricingAsync(
            request.CardCode,
            request.ItemCodes,
            cancellationToken);

        if (pricing is null)
        {
            return Errors.BusinessPartner.NotFound(request.CardCode);
        }

        logger.LogInformation(
            "Retrieved {Count} locally stored item prices for business partner {CardCode} using price list {PriceListNum}",
            pricing.Prices.TotalCount,
            request.CardCode,
            pricing.Prices.PriceListNum);

        return pricing.Prices;
    }
}
