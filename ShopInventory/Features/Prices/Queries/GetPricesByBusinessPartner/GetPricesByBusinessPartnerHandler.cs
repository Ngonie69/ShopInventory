using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Prices.Queries.GetPricesByBusinessPartner;

public sealed class GetPricesByBusinessPartnerHandler(
    ILocalPriceCatalogService localPriceCatalogService,
    ISAPServiceLayerClient sapServiceLayerClient,
    ILogger<GetPricesByBusinessPartnerHandler> logger)
    : IRequestHandler<GetPricesByBusinessPartnerQuery, ErrorOr<ItemPricesByListResponseDto>>
{
    public async Task<ErrorOr<ItemPricesByListResponseDto>> Handle(
        GetPricesByBusinessPartnerQuery request, CancellationToken cancellationToken)
    {
        var normalizedCardCode = request.CardCode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCardCode))
        {
            return Errors.BusinessPartner.NotFound(request.CardCode);
        }

        var normalizedItemCodes = NormalizeItemCodes(request.ItemCodes);

        if (request.UseLivePricing)
        {
            try
            {
                var livePricing = await GetLivePricingAsync(
                    normalizedCardCode,
                    normalizedItemCodes,
                    sapServiceLayerClient,
                    cancellationToken);

                if (livePricing is not null)
                {
                    logger.LogInformation(
                        "Retrieved {Count} live SAP item prices for business partner {CardCode} using price list {PriceListNum}",
                        livePricing.TotalCount,
                        normalizedCardCode,
                        livePricing.PriceListNum);

                    return livePricing;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to retrieve live SAP pricing for business partner {CardCode}; falling back to locally stored prices",
                    normalizedCardCode);
            }
        }

        var pricing = await localPriceCatalogService.GetBusinessPartnerPricingAsync(
            normalizedCardCode,
            normalizedItemCodes,
            cancellationToken);

        if (pricing is null)
        {
            return Errors.BusinessPartner.NotFound(normalizedCardCode);
        }

        if (request.UseLivePricing)
        {
            logger.LogWarning(
                "Retrieved {Count} locally stored item prices for business partner {CardCode} using price list {PriceListNum} after live SAP pricing was unavailable",
                pricing.Prices.TotalCount,
                normalizedCardCode,
                pricing.Prices.PriceListNum);
        }
        else
        {
            logger.LogInformation(
                "Retrieved {Count} locally stored item prices for business partner {CardCode} using price list {PriceListNum}",
                pricing.Prices.TotalCount,
                normalizedCardCode,
                pricing.Prices.PriceListNum);
        }

        return pricing.Prices;
    }

    private static async Task<ItemPricesByListResponseDto?> GetLivePricingAsync(
        string cardCode,
        IReadOnlyCollection<string> itemCodes,
        ISAPServiceLayerClient sapServiceLayerClient,
        CancellationToken cancellationToken)
    {
        var businessPartner = await sapServiceLayerClient.GetBusinessPartnerByCodeAsync(cardCode, cancellationToken);
        if (businessPartner?.PriceListNum is not > 0)
        {
            return null;
        }

        var priceListNum = businessPartner.PriceListNum.Value;
        var livePrices = itemCodes.Count > 0
            ? await sapServiceLayerClient.GetItemPricesForCustomerAsync(cardCode, itemCodes, cancellationToken)
            : await sapServiceLayerClient.GetPricesByPriceListAsync(priceListNum, cancellationToken);

        var specialPrices = itemCodes.Count > 0
            ? await sapServiceLayerClient.GetSpecialPricesForBPAsync(cardCode, itemCodes, cancellationToken)
            : await sapServiceLayerClient.GetSpecialPricesForBPAsync(cardCode, cancellationToken);

        var mergedPrices = MergePrices(
            livePrices,
            specialPrices,
            priceListNum,
            businessPartner.PriceListName,
            businessPartner.Currency);

        return new ItemPricesByListResponseDto
        {
            TotalCount = mergedPrices.Count,
            PriceListNum = priceListNum,
            PriceListName = ResolvePriceListName(priceListNum, businessPartner.PriceListName, mergedPrices),
            Currency = ResolveCurrency(businessPartner.Currency, mergedPrices),
            Prices = mergedPrices
        };
    }

    private static List<ItemPriceByListDto> MergePrices(
        IEnumerable<ItemPriceByListDto> livePrices,
        IReadOnlyDictionary<string, decimal> specialPrices,
        int priceListNum,
        string? priceListName,
        string? currency)
    {
        var mergedPrices = livePrices
            .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode))
            .GroupBy(price => price.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.FirstOrDefault(price => price.Price > 0) ?? group.First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var specialPrice in specialPrices)
        {
            if (mergedPrices.TryGetValue(specialPrice.Key, out var existingPrice))
            {
                existingPrice.Price = specialPrice.Value;
                continue;
            }

            mergedPrices[specialPrice.Key] = new ItemPriceByListDto
            {
                ItemCode = specialPrice.Key,
                Price = specialPrice.Value,
                PriceListNum = priceListNum,
                PriceListName = priceListName,
                Currency = currency
            };
        }

        return mergedPrices.Values
            .OrderBy(price => price.ItemCode)
            .ToList();
    }

    private static string ResolvePriceListName(
        int priceListNum,
        string? businessPartnerPriceListName,
        IReadOnlyCollection<ItemPriceByListDto> prices)
    {
        return prices.FirstOrDefault(price => !string.IsNullOrWhiteSpace(price.PriceListName))?.PriceListName
            ?? businessPartnerPriceListName
            ?? $"Price List {priceListNum}";
    }

    private static string? ResolveCurrency(
        string? businessPartnerCurrency,
        IReadOnlyCollection<ItemPriceByListDto> prices)
    {
        return prices.FirstOrDefault(price => !string.IsNullOrWhiteSpace(price.Currency))?.Currency
            ?? businessPartnerCurrency;
    }

    private static List<string> NormalizeItemCodes(IReadOnlyCollection<string>? itemCodes)
        => itemCodes?
            .Where(itemCode => !string.IsNullOrWhiteSpace(itemCode))
            .Select(itemCode => itemCode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
}
