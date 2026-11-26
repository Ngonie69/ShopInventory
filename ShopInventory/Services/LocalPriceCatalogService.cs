using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Pricing;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public sealed class LocalPriceCatalogService(
    ApplicationDbContext context) : ILocalPriceCatalogService
{
    public async Task<PriceListsResponseDto> GetPriceListsAsync(CancellationToken cancellationToken = default)
    {
        var priceLists = await context.PriceLists
            .AsNoTracking()
            .Where(priceList => priceList.IsActive)
            .OrderBy(priceList => priceList.ListNum)
            .Select(priceList => new PriceListDto
            {
                ListNum = priceList.ListNum,
                ListName = priceList.ListName,
                BasePriceList = priceList.BasePriceList.HasValue ? priceList.BasePriceList.Value.ToString() : null,
                Currency = priceList.Currency,
                IsActive = priceList.IsActive,
                Factor = priceList.Factor,
                RoundingMethod = priceList.RoundingMethod,
                LastSyncedAt = priceList.LastSyncedAt
            })
            .ToListAsync(cancellationToken);

        return new PriceListsResponseDto
        {
            TotalCount = priceLists.Count,
            PriceLists = priceLists
        };
    }

    public async Task<ItemPricesResponseDto> GetAllPricesAsync(CancellationToken cancellationToken = default)
    {
        var prices = await BuildActiveItemPriceQuery()
            .OrderBy(price => price.ItemCode)
            .ThenBy(price => price.PriceList)
            .Select(price => new ItemPriceDto
            {
                ItemCode = price.ItemCode,
                ItemName = price.ItemName,
                Price = price.Price,
                Currency = price.Currency,
                PriceListNum = price.PriceList,
                PriceListName = price.PriceListName
            })
            .ToListAsync(cancellationToken);

        return BuildPriceResponse(prices);
    }

    public async Task<ItemPricesGroupedResponseDto> GetGroupedPricesAsync(CancellationToken cancellationToken = default)
    {
        var prices = await BuildActiveItemPriceQuery()
            .OrderBy(price => price.ItemCode)
            .ThenBy(price => price.PriceList)
            .Select(price => new ItemPriceDto
            {
                ItemCode = price.ItemCode,
                ItemName = price.ItemName,
                Price = price.Price,
                Currency = price.Currency,
                PriceListNum = price.PriceList,
                PriceListName = price.PriceListName
            })
            .ToListAsync(cancellationToken);

        var groupedItems = prices
            .GroupBy(price => price.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ItemPriceGroupedDto
            {
                ItemCode = group.First().ItemCode,
                ItemName = group.First().ItemName,
                UsdPrice = group
                    .Where(price => string.Equals(price.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(price => price.PriceListNum)
                    .Select(price => (decimal?)price.Price)
                    .FirstOrDefault(),
                ZigPrice = group
                    .Where(price => string.Equals(price.Currency, "ZIG", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(price => price.PriceListNum)
                    .Select(price => (decimal?)price.Price)
                    .FirstOrDefault()
            })
            .OrderBy(item => item.ItemCode)
            .ToList();

        return new ItemPricesGroupedResponseDto
        {
            TotalItems = groupedItems.Count,
            Items = groupedItems
        };
    }

    public async Task<ItemPriceGroupedDto?> GetGroupedPriceByItemCodeAsync(string itemCode, CancellationToken cancellationToken = default)
    {
        var normalizedItemCode = itemCode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedItemCode))
        {
            return null;
        }

        var prices = await BuildActiveItemPriceQuery()
            .Where(price => price.ItemCode == normalizedItemCode)
            .OrderBy(price => price.PriceList)
            .Select(price => new ItemPriceDto
            {
                ItemCode = price.ItemCode,
                ItemName = price.ItemName,
                Price = price.Price,
                Currency = price.Currency,
                PriceListNum = price.PriceList,
                PriceListName = price.PriceListName
            })
            .ToListAsync(cancellationToken);

        if (prices.Count == 0)
        {
            return null;
        }

        return new ItemPriceGroupedDto
        {
            ItemCode = prices[0].ItemCode,
            ItemName = prices[0].ItemName,
            UsdPrice = prices
                .Where(price => string.Equals(price.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                .OrderBy(price => price.PriceListNum)
                .Select(price => (decimal?)price.Price)
                .FirstOrDefault(),
            ZigPrice = prices
                .Where(price => string.Equals(price.Currency, "ZIG", StringComparison.OrdinalIgnoreCase))
                .OrderBy(price => price.PriceListNum)
                .Select(price => (decimal?)price.Price)
                .FirstOrDefault()
        };
    }

    public async Task<ItemPricesResponseDto> GetPricesByCurrencyAsync(string currency, CancellationToken cancellationToken = default)
    {
        var normalizedCurrency = currency.Trim().ToUpperInvariant();

        var prices = await BuildActiveItemPriceQuery()
            .Where(price => price.Currency != null && price.Currency.ToUpper() == normalizedCurrency)
            .OrderBy(price => price.ItemCode)
            .ThenBy(price => price.PriceList)
            .Select(price => new ItemPriceDto
            {
                ItemCode = price.ItemCode,
                ItemName = price.ItemName,
                Price = price.Price,
                Currency = price.Currency,
                PriceListNum = price.PriceList,
                PriceListName = price.PriceListName
            })
            .ToListAsync(cancellationToken);

        return BuildPriceResponse(prices);
    }

    public async Task<ItemPricesByListResponseDto> GetPricesByPriceListAsync(
        int priceListNum,
        IReadOnlyCollection<string>? itemCodes = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedItemCodes = NormalizeItemCodes(itemCodes);

        var priceList = await context.PriceLists
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.ListNum == priceListNum, cancellationToken);

        var query = BuildActiveItemPriceQuery()
            .Where(price => price.PriceList == priceListNum);

        if (normalizedItemCodes.Count > 0)
        {
            query = query.Where(price => normalizedItemCodes.Contains(price.ItemCode));
        }

        var prices = await query
            .OrderBy(price => price.ItemCode)
            .Select(price => new ItemPriceByListDto
            {
                ItemCode = price.ItemCode,
                ItemName = price.ItemName,
                ForeignName = price.ForeignName,
                Price = price.Price,
                PriceListNum = price.PriceList,
                PriceListName = price.PriceListName,
                Currency = price.Currency,
                BasePriceList = price.BasePriceList,
                Factor = price.Factor
            })
            .ToListAsync(cancellationToken);

        return new ItemPricesByListResponseDto
        {
            TotalCount = prices.Count,
            PriceListNum = priceListNum,
            PriceListName = priceList?.ListName ?? prices.FirstOrDefault()?.PriceListName ?? $"Price List {priceListNum}",
            Currency = priceList?.Currency ?? prices.FirstOrDefault()?.Currency,
            Prices = prices
        };
    }

    public async Task<ItemPriceByListDto?> GetItemPriceFromListAsync(
        int priceListNum,
        string itemCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedItemCode = itemCode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedItemCode))
        {
            return null;
        }

        return await BuildActiveItemPriceQuery()
            .Where(price => price.PriceList == priceListNum && price.ItemCode == normalizedItemCode)
            .Select(price => new ItemPriceByListDto
            {
                ItemCode = price.ItemCode,
                ItemName = price.ItemName,
                ForeignName = price.ForeignName,
                Price = price.Price,
                PriceListNum = price.PriceList,
                PriceListName = price.PriceListName,
                Currency = price.Currency,
                BasePriceList = price.BasePriceList,
                Factor = price.Factor
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Dictionary<string, decimal>> GetActiveSpecialPricesAsync(
        string cardCode,
        IReadOnlyCollection<string>? itemCodes = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCardCode = cardCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedCardCode))
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        // The customer's price list, so its period and volume discounts count too. Without a
        // synced profile only the customer's own rows can be read.
        var priceListNum = await context.BusinessPartnerPriceProfiles
            .AsNoTracking()
            .Where(profile => profile.SyncedFromSAP && profile.CardCode == normalizedCardCode)
            .Select(profile => (int?)profile.PriceListNum)
            .SingleOrDefaultAsync(cancellationToken);

        return await GetCurrentSpecialPricesAsync(
            normalizedCardCode,
            priceListNum,
            NormalizeItemCodes(itemCodes),
            DateTime.UtcNow.Date,
            cancellationToken);
    }

    /// <summary>
    /// One special price per item that applies to this customer today — its own where it has one,
    /// otherwise its price list's period and volume discount. See <see cref="SpecialPriceScope"/>.
    /// </summary>
    private async Task<Dictionary<string, decimal>> GetCurrentSpecialPricesAsync(
        string normalizedCardCode,
        int? priceListNum,
        IReadOnlyCollection<string> normalizedItemCodes,
        DateTime todayUtc,
        CancellationToken cancellationToken)
    {
        var rows = await BuildCurrentSpecialPriceQuery(
                SpecialPriceScope.CardCodes(normalizedCardCode, priceListNum),
                normalizedItemCodes,
                todayUtc)
            .Select(price => new { price.CardCode, price.ItemCode, price.Price })
            .ToListAsync(cancellationToken);

        return SpecialPriceScope.Resolve(
            normalizedCardCode,
            rows.Select(row => (row.CardCode, row.ItemCode, row.Price)));
    }

    /// <summary>
    /// The single definition of "a special price that applies today" — synced from SAP, active,
    /// priced above zero, and inside its validity window. Shared so the pricing merge and the
    /// standalone lookup cannot drift apart on what counts as current.
    /// </summary>
    /// <remarks>
    /// A zero (or negative) row means the price never made it across from SAP, not that the
    /// customer gets the goods free, so it is excluded here rather than at each call site —
    /// applying one would put the document line through at 0.00.
    /// </remarks>
    private IQueryable<BusinessPartnerSpecialPriceEntity> BuildCurrentSpecialPriceQuery(
        IReadOnlyCollection<string> cardCodes,
        IReadOnlyCollection<string> normalizedItemCodes,
        DateTime todayUtc)
    {
        // A plain list, so the provider sees an ordinary parameter collection.
        var cardCodeList = cardCodes.ToList();

        var query = context.BusinessPartnerSpecialPrices
            .AsNoTracking()
            .Where(price =>
                price.SyncedFromSAP &&
                price.IsActive &&
                price.Price > 0 &&
                cardCodeList.Contains(price.CardCode) &&
                (!price.ValidFrom.HasValue || price.ValidFrom <= todayUtc) &&
                (!price.ValidTo.HasValue || price.ValidTo >= todayUtc));

        if (normalizedItemCodes.Count > 0)
        {
            query = query.Where(price => normalizedItemCodes.Contains(price.ItemCode));
        }

        return query.OrderBy(price => price.ItemCode);
    }

    public async Task<LocalBusinessPartnerPricingResult?> GetBusinessPartnerPricingAsync(
        string cardCode,
        IReadOnlyCollection<string>? itemCodes = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCardCode = cardCode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCardCode))
        {
            return null;
        }

        var businessPartnerProfile = await context.BusinessPartnerPriceProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(profile =>
                profile.SyncedFromSAP &&
                profile.CardCode == normalizedCardCode,
                cancellationToken);

        if (businessPartnerProfile is null)
        {
            return null;
        }

        var normalizedItemCodes = NormalizeItemCodes(itemCodes);
        var priceListNum = businessPartnerProfile.PriceListNum;
        var priceListPrices = await GetPricesByPriceListAsync(priceListNum, normalizedItemCodes, cancellationToken);
        var specialPrices = await GetCurrentSpecialPricesAsync(
            normalizedCardCode,
            priceListNum,
            normalizedItemCodes,
            DateTime.UtcNow.Date,
            cancellationToken);

        var mergedPrices = (priceListPrices.Prices ?? [])
            .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode))
            .ToDictionary(price => price.ItemCode!, price => price, StringComparer.OrdinalIgnoreCase);

        foreach (var specialPrice in specialPrices)
        {
            if (mergedPrices.TryGetValue(specialPrice.Key, out var existingPrice))
            {
                // Same as the live SAP path: keep the price list price and the discount that gets us
                // to the special price, so the document line can be shown the way SAP shows it.
                var listPrice = existingPrice.Price;
                existingPrice.Price = specialPrice.Value;
                existingPrice.DiscountPercent = SpecialPriceDiscount.CalculatePercent(listPrice, specialPrice.Value);
                existingPrice.PriceBeforeDiscount = existingPrice.DiscountPercent.HasValue ? listPrice : null;
                continue;
            }

            mergedPrices[specialPrice.Key] = new ItemPriceByListDto
            {
                ItemCode = specialPrice.Key,
                Price = specialPrice.Value,
                PriceListNum = priceListNum,
                PriceListName = priceListPrices.PriceListName,
                Currency = priceListPrices.Currency
            };
        }

        var mergedPriceList = mergedPrices.Values
            .OrderBy(price => price.ItemCode)
            .ToList();

        return new LocalBusinessPartnerPricingResult
        {
            BusinessPartner = new BusinessPartnerDto
            {
                CardCode = businessPartnerProfile.CardCode,
                CardName = businessPartnerProfile.CardName,
                Currency = businessPartnerProfile.Currency,
                IsActive = businessPartnerProfile.IsActive,
                PriceListNum = businessPartnerProfile.PriceListNum
            },
            Prices = new ItemPricesByListResponseDto
            {
                TotalCount = mergedPriceList.Count,
                PriceListNum = priceListNum,
                PriceListName = priceListPrices.PriceListName ?? businessPartnerProfile.CardName ?? $"Price List {priceListNum}",
                Currency = priceListPrices.Currency,
                Prices = mergedPriceList
            }
        };
    }

    private IQueryable<ItemPriceEntity> BuildActiveItemPriceQuery()
        => context.ItemPrices
            .AsNoTracking()
            .Where(price => price.SyncedFromSAP && price.IsActive);

    private static ItemPricesResponseDto BuildPriceResponse(List<ItemPriceDto> prices)
        => new()
        {
            TotalCount = prices.Count,
            UsdPriceCount = prices.Count(price => string.Equals(price.Currency, "USD", StringComparison.OrdinalIgnoreCase)),
            ZigPriceCount = prices.Count(price => string.Equals(price.Currency, "ZIG", StringComparison.OrdinalIgnoreCase)),
            Prices = prices
        };

    private static List<string> NormalizeItemCodes(IReadOnlyCollection<string>? itemCodes)
        => itemCodes?
            .Where(itemCode => !string.IsNullOrWhiteSpace(itemCode))
            .Select(itemCode => itemCode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
}
