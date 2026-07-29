using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// A customer's price for one item, split the way SAP splits it on a document line: a unit price
/// plus a discount percent. <see cref="NetPrice"/> is what the customer actually pays and is carried
/// through rather than recomputed, so a six-decimal discount cannot drift it off the special price.
/// </summary>
public sealed record ResolvedItemPrice(decimal UnitPrice, decimal DiscountPercent, decimal NetPrice)
{
    public static ResolvedItemPrice Flat(decimal price) => new(price, 0m, price);
}

public sealed class BusinessPartnerPriceLookup
{
    public string? CardCode { get; init; }
    public int? PriceListNum { get; init; }
    public Dictionary<string, decimal> Prices { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ResolvedItemPrice> Pricing { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IBusinessPartnerPricingService
{
    Task<BusinessPartnerPriceLookup> LoadPriceLookupAsync(BusinessPartnerDto? businessPartner, CancellationToken cancellationToken = default);

    Task<decimal> ResolveUnitPriceAsync(
        string? itemCode,
        string? currency,
        decimal defaultPrice,
        string? cardCode = null,
        int? priceListNum = null,
        IReadOnlyDictionary<string, decimal>? knownPrices = null,
        CancellationToken cancellationToken = default);

    Task<ResolvedItemPrice> ResolveItemPriceAsync(
        string? itemCode,
        string? currency,
        decimal defaultPrice,
        string? cardCode = null,
        int? priceListNum = null,
        IReadOnlyDictionary<string, ResolvedItemPrice>? knownPricing = null,
        CancellationToken cancellationToken = default);
}

public sealed class BusinessPartnerPricingService : IBusinessPartnerPricingService
{
    private readonly IPriceService _priceService;
    private readonly ILogger<BusinessPartnerPricingService> _logger;

    public BusinessPartnerPricingService(
        IPriceService priceService,
        ILogger<BusinessPartnerPricingService> logger)
    {
        _priceService = priceService;
        _logger = logger;
    }

    public async Task<BusinessPartnerPriceLookup> LoadPriceLookupAsync(
        BusinessPartnerDto? businessPartner,
        CancellationToken cancellationToken = default)
    {
        var cardCode = businessPartner?.CardCode?.Trim();
        var priceListNum = businessPartner?.PriceListNum;

        if (string.IsNullOrWhiteSpace(cardCode))
        {
            return new BusinessPartnerPriceLookup
            {
                PriceListNum = priceListNum
            };
        }

        try
        {
            var bpResponse = await _priceService.GetPricesByBusinessPartnerAsync(cardCode);
            var bpPriceMap = ToPriceMap(bpResponse?.Prices);
            if (bpPriceMap.Count > 0)
            {
                return new BusinessPartnerPriceLookup
                {
                    CardCode = cardCode,
                    PriceListNum = bpResponse?.PriceListNum > 0 ? bpResponse.PriceListNum : priceListNum,
                    Prices = bpPriceMap,
                    Pricing = ToPricingMap(bpResponse?.Prices)
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load BP pricing for {CardCode}; falling back to price list data", cardCode);
        }

        if (priceListNum is > 0)
        {
            var fallbackPrices = await LoadPriceListAsync(priceListNum.Value, cancellationToken);
            return new BusinessPartnerPriceLookup
            {
                CardCode = cardCode,
                PriceListNum = priceListNum,
                Prices = ToPriceMap(fallbackPrices),
                Pricing = ToPricingMap(fallbackPrices)
            };
        }

        return new BusinessPartnerPriceLookup
        {
            CardCode = cardCode,
            PriceListNum = priceListNum
        };
    }

    public async Task<decimal> ResolveUnitPriceAsync(
        string? itemCode,
        string? currency,
        decimal defaultPrice,
        string? cardCode = null,
        int? priceListNum = null,
        IReadOnlyDictionary<string, decimal>? knownPrices = null,
        CancellationToken cancellationToken = default)
    {
        var knownPricing = knownPrices?.ToDictionary(
            entry => entry.Key,
            entry => ResolvedItemPrice.Flat(entry.Value),
            StringComparer.OrdinalIgnoreCase);

        var resolved = await ResolveItemPriceAsync(
            itemCode,
            currency,
            defaultPrice,
            cardCode,
            priceListNum,
            knownPricing,
            cancellationToken);

        return resolved.NetPrice;
    }

    public async Task<ResolvedItemPrice> ResolveItemPriceAsync(
        string? itemCode,
        string? currency,
        decimal defaultPrice,
        string? cardCode = null,
        int? priceListNum = null,
        IReadOnlyDictionary<string, ResolvedItemPrice>? knownPricing = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedItemCode = itemCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedItemCode))
        {
            return ResolvedItemPrice.Flat(defaultPrice);
        }

        if (knownPricing is not null && knownPricing.TryGetValue(normalizedItemCode, out var knownPrice))
        {
            return knownPrice;
        }

        var normalizedCardCode = cardCode?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedCardCode))
        {
            try
            {
                var bpResponse = await _priceService.GetPricesByBusinessPartnerAsync(normalizedCardCode, [normalizedItemCode]);
                var bpPrice = FindItemPrice(bpResponse?.Prices, normalizedItemCode);
                if (bpPrice is not null)
                {
                    return ToResolvedPrice(bpPrice);
                }

                if (priceListNum is null && bpResponse?.PriceListNum > 0)
                {
                    priceListNum = bpResponse.PriceListNum;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed targeted BP pricing lookup for {CardCode}/{ItemCode}", normalizedCardCode, normalizedItemCode);
            }
        }

        if (priceListNum is > 0)
        {
            var priceListPrices = await LoadPriceListAsync(priceListNum.Value, cancellationToken);
            var priceListPrice = FindItemPrice(priceListPrices, normalizedItemCode);
            if (priceListPrice is not null)
            {
                return ToResolvedPrice(priceListPrice);
            }
        }

        try
        {
            var groupedPrice = await _priceService.GetPriceByItemCodeAsync(normalizedItemCode);
            var fallbackPrice = GetCurrencyPrice(groupedPrice, currency);
            if (fallbackPrice.HasValue)
            {
                return ResolvedItemPrice.Flat(fallbackPrice.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed grouped price lookup for {ItemCode}", normalizedItemCode);
        }

        return ResolvedItemPrice.Flat(defaultPrice);
    }

    private async Task<List<ItemPriceByListDto>> LoadPriceListAsync(int priceListNum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var response = await _priceService.GetPricesByPriceListAsync(priceListNum);
        return response?.Prices ?? [];
    }

    private static Dictionary<string, decimal> ToPriceMap(IEnumerable<ItemPriceByListDto>? prices)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (prices is null)
        {
            return result;
        }

        foreach (var price in prices)
        {
            if (!string.IsNullOrWhiteSpace(price.ItemCode))
            {
                result[price.ItemCode.Trim()] = price.Price;
            }
        }

        return result;
    }

    private static Dictionary<string, ResolvedItemPrice> ToPricingMap(IEnumerable<ItemPriceByListDto>? prices)
    {
        var result = new Dictionary<string, ResolvedItemPrice>(StringComparer.OrdinalIgnoreCase);
        if (prices is null)
        {
            return result;
        }

        foreach (var price in prices)
        {
            if (!string.IsNullOrWhiteSpace(price.ItemCode))
            {
                result[price.ItemCode.Trim()] = ToResolvedPrice(price);
            }
        }

        return result;
    }

    private static ResolvedItemPrice ToResolvedPrice(ItemPriceByListDto price)
    {
        return price.DiscountPercent is > 0 && price.PriceBeforeDiscount is > 0
            ? new ResolvedItemPrice(price.PriceBeforeDiscount.Value, price.DiscountPercent.Value, price.Price)
            : ResolvedItemPrice.Flat(price.Price);
    }

    private static ItemPriceByListDto? FindItemPrice(IEnumerable<ItemPriceByListDto>? prices, string itemCode)
    {
        return prices?
            .FirstOrDefault(price => itemCode.Equals(price.ItemCode?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static decimal? GetCurrencyPrice(ItemPriceGroupedDto? priceInfo, string? currency)
    {
        if (priceInfo is null)
        {
            return null;
        }

        return currency?.Trim().ToUpperInvariant() switch
        {
            "ZIG" => priceInfo.ZigPrice ?? priceInfo.UsdPrice,
            "USD" => priceInfo.UsdPrice ?? priceInfo.ZigPrice,
            _ => priceInfo.UsdPrice ?? priceInfo.ZigPrice
        };
    }
}