using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesOrders;

/// <inheritdoc />
/// <remarks>
/// Local sources only. The item list is the active <c>MerchandiserProducts</c> — already the one
/// shared list of what may be sold on a handset — and prices come from
/// <see cref="ILocalPriceCatalogService"/>, the synced copy of the SAP price list. Nothing here
/// calls SAP: a shopkeeper opening the app must not be told the shop is shut because the Service
/// Layer is slow.
/// </remarks>
public sealed class VanSalesCatalogueReader(
    ApplicationDbContext context,
    ILocalPriceCatalogService priceCatalog,
    IVanSalesOrderingPolicy orderingPolicy,
    IOptions<TaxSettings> taxSettings,
    ILogger<VanSalesCatalogueReader> logger) : IVanSalesCatalogueReader
{
    private readonly TaxSettings _tax = taxSettings.Value;

    public async Task<VanSalesPricedCatalogue> ReadAsync(CancellationToken cancellationToken)
    {
        var rules = await orderingPolicy.GetRulesAsync(cancellationToken);

        // A row per merchandiser per item, so the same item appears many times. Deduplicated by
        // code the way the merchandiser queries do — a duplicated catalogue line is a shopkeeper
        // adding the same thing twice.
        var products = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Id)
            .Select(p => new
            {
                p.ItemCode,
                p.ItemName,
                p.BarCode,
                p.UoM,
                p.Category
            })
            .ToListAsync(cancellationToken);

        var productByCode = products
            .GroupBy(p => p.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (productByCode.Count == 0)
        {
            logger.LogWarning("The van sales customer catalogue is empty: no merchandiser products are active.");
            return new VanSalesPricedCatalogue(
                rules.PriceListNumber,
                null,
                new Dictionary<string, VanSalesPricedItem>(StringComparer.OrdinalIgnoreCase));
        }

        var priced = await priceCatalog.GetPricesByPriceListAsync(
            rules.PriceListNumber,
            productByCode.Keys.ToList(),
            cancellationToken);

        var priceByCode = (priced.Prices ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.ItemCode))
            .GroupBy(p => p.ItemCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Price, StringComparer.OrdinalIgnoreCase);

        var taxPercent = NormaliseTaxPercent(_tax.VatRate);

        var items = new Dictionary<string, VanSalesPricedItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, product) in productByCode)
        {
            if (!priceByCode.TryGetValue(code, out var unitPrice) || unitPrice <= 0)
            {
                // No price on the configured list, or a zero one. Left out rather than offered at
                // nothing: an item a customer can add to a basket for free is an order somebody
                // unpicks by hand.
                continue;
            }

            items[code] = new VanSalesPricedItem(
                code,
                product.ItemName ?? code,
                product.BarCode,
                product.UoM,
                product.Category,
                unitPrice,
                taxPercent);
        }

        if (items.Count < productByCode.Count)
        {
            logger.LogInformation(
                "Left {Missing} of {Total} active products out of the van sales customer catalogue: no price on list {PriceList}.",
                productByCode.Count - items.Count,
                productByCode.Count,
                rules.PriceListNumber);
        }

        return new VanSalesPricedCatalogue(rules.PriceListNumber, priced.Currency, items);
    }

    /// <summary>
    /// Tax as a percentage, matching how <c>SalesOrderService</c> normalises the same setting —
    /// the config holds 0.155 and the documents hold 15.5.
    /// </summary>
    private static decimal NormaliseTaxPercent(decimal configuredVatRate)
        => configuredVatRate <= 1 ? configuredVatRate * 100m : configuredVatRate;
}
