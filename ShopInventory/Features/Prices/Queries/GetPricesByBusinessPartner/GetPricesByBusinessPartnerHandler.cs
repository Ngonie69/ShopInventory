using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPricesByBusinessPartner;

public sealed class GetPricesByBusinessPartnerHandler(
    IBusinessPartnerService businessPartnerService,
    ISAPServiceLayerClient sapClient,
    ILogger<GetPricesByBusinessPartnerHandler> logger)
    : IRequestHandler<GetPricesByBusinessPartnerQuery, ErrorOr<ItemPricesByListResponseDto>>
{
    public async Task<ErrorOr<ItemPricesByListResponseDto>> Handle(
        GetPricesByBusinessPartnerQuery request, CancellationToken cancellationToken)
    {
        var bp = await businessPartnerService.GetBusinessPartnerByCodeAsync(request.CardCode, cancellationToken);
        if (bp is null)
            return Error.NotFound("BusinessPartner.NotFound", $"Business partner '{request.CardCode}' not found in SAP.");

        var priceListNum = bp.PriceListNum ?? 1; // Default to price list 1 if none assigned

        var prices = await sapClient.GetPricesByPriceListAsync(priceListNum, cancellationToken);

        // Overlay BP-specific special prices (OSPP) on top of the price list prices
        var specialPrices = await sapClient.GetSpecialPricesForBPAsync(request.CardCode, cancellationToken);
        if (specialPrices.Count > 0)
        {
            logger.LogInformation("Applying {Count} special prices for BP {CardCode} over price list {PriceListNum}",
                specialPrices.Count, request.CardCode, priceListNum);

            foreach (var price in prices)
            {
                if (price.ItemCode is not null && specialPrices.TryGetValue(price.ItemCode, out var specialPrice))
                    price.Price = specialPrice;
            }

            // Add special-priced items that aren't in the base price list
            var existingCodes = prices.Select(p => p.ItemCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (itemCode, specialPrice) in specialPrices)
            {
                if (!existingCodes.Contains(itemCode))
                {
                    prices.Add(new ItemPriceByListDto
                    {
                        ItemCode = itemCode,
                        Price = specialPrice,
                        PriceListNum = priceListNum,
                        PriceListName = $"Special Price ({request.CardCode})"
                    });
                }
            }
        }

        return new ItemPricesByListResponseDto
        {
            TotalCount = prices.Count,
            PriceListNum = priceListNum,
            PriceListName = bp.CardName ?? $"Price List {priceListNum}",
            Currency = prices.FirstOrDefault()?.Currency,
            Prices = prices
        };
    }
}
