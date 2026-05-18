using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public sealed class BusinessPartnerPriceLookup
{
    public string? CardCode { get; init; }
    public int? PriceListNum { get; init; }
    public Dictionary<string, decimal> Prices { get; init; } = new(StringComparer.OrdinalIgnoreCase);
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
                    Prices = bpPriceMap
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load BP pricing for {CardCode}; falling back to price list data", cardCode);
        }

        if (priceListNum is > 0)
        {
            var fallbackPriceMap = await LoadPriceListMapAsync(priceListNum.Value, cancellationToken);
            return new BusinessPartnerPriceLookup
            {
                CardCode = cardCode,
                PriceListNum = priceListNum,
                Prices = fallbackPriceMap
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
        var normalizedItemCode = itemCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedItemCode))
        {
            return defaultPrice;
        }

        if (TryGetKnownPrice(knownPrices, normalizedItemCode, out var knownPrice))
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
                if (bpPrice.HasValue)
                {
                    return bpPrice.Value;
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
            var priceListPrices = await LoadPriceListMapAsync(priceListNum.Value, cancellationToken);
            if (priceListPrices.TryGetValue(normalizedItemCode, out var priceListPrice))
            {
                return priceListPrice;
            }
        }

        try
        {
            var groupedPrice = await _priceService.GetPriceByItemCodeAsync(normalizedItemCode);
            var fallbackPrice = GetCurrencyPrice(groupedPrice, currency);
            if (fallbackPrice.HasValue)
            {
                return fallbackPrice.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed grouped price lookup for {ItemCode}", normalizedItemCode);
        }

        return defaultPrice;
    }

    private async Task<Dictionary<string, decimal>> LoadPriceListMapAsync(int priceListNum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var response = await _priceService.GetPricesByPriceListAsync(priceListNum);
        return ToPriceMap(response?.Prices);
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

    private static decimal? FindItemPrice(IEnumerable<ItemPriceByListDto>? prices, string itemCode)
    {
        return prices?
            .FirstOrDefault(price => itemCode.Equals(price.ItemCode, StringComparison.OrdinalIgnoreCase))?
            .Price;
    }

    private static bool TryGetKnownPrice(IReadOnlyDictionary<string, decimal>? prices, string itemCode, out decimal price)
    {
        if (prices is not null && prices.TryGetValue(itemCode, out price))
        {
            return true;
        }

        price = 0;
        return false;
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