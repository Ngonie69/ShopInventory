using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerCatalogue;

/// <summary>
/// Builds the priced catalogue a customer browses, with a stock band per item.
/// </summary>
/// <remarks>
/// The item list and its prices come from <see cref="IVanSalesCatalogueReader"/>, which is also
/// what order intake prices against — so an item shown here at a price is accepted there at the
/// same price, and neither side can quietly offer something the other refuses.
/// <para>
/// The stock band is added here rather than in the reader because it is presentation, and the only
/// part of this that order intake has no use for. It comes from the daily stock snapshot, a local
/// projection, rather than from <c>GetStockForItemsInWarehouse</c> — that is a live SAP call capped
/// at a hundred items which fails outright when the integration is disabled, and a shopkeeper
/// opening the app must not be told the shop is shut because the Service Layer is slow.
/// </para>
/// </remarks>
public sealed class GetVanSalesCustomerCatalogueHandler(
    ApplicationDbContext context,
    IVanSalesCatalogueReader catalogueReader,
    IVanSalesOrderingPolicy orderingPolicy,
    ILogger<GetVanSalesCustomerCatalogueHandler> logger)
    : IRequestHandler<GetVanSalesCustomerCatalogueQuery, ErrorOr<VanSalesCatalogueResult>>
{
    public async Task<ErrorOr<VanSalesCatalogueResult>> Handle(
        GetVanSalesCustomerCatalogueQuery query,
        CancellationToken cancellationToken)
    {
        var account = await context.VanSalesCustomerAccounts
            .AsNoTracking()
            .Where(a => a.Id == query.AccountId && a.IsActive && a.RouteCustomer != null)
            .Select(a => new
            {
                a.RouteCustomer!.AssignedBusinessPartnerCode,
                CustomerActive = a.RouteCustomer.IsActive
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (account is null || !account.CustomerActive)
        {
            return Errors.VanSalesCustomerAuth.AccountInactive;
        }

        var rules = await orderingPolicy.GetRulesAsync(cancellationToken);
        var catalogue = await catalogueReader.ReadAsync(cancellationToken);

        var warehouseCode = await ResolveStockWarehouseAsync(
            account.AssignedBusinessPartnerCode,
            cancellationToken);

        var available = await ReadAvailableQuantitiesAsync(warehouseCode, cancellationToken);

        var items = catalogue.ItemsByCode.Values
            .OrderBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(item => new VanSalesCatalogueItem(
                item.ItemCode,
                item.ItemName,
                item.BarCode,
                item.UnitOfMeasure,
                item.Category,
                item.UnitPrice,
                item.TaxPercent,
                Math.Round(item.UnitPrice * (1 + item.TaxPercent / 100m), 2, MidpointRounding.AwayFromZero),
                BandFor(item.ItemCode, warehouseCode, available, rules.LowStockThreshold)))
            .ToList();

        return new VanSalesCatalogueResult(
            catalogue.PriceListNumber,
            catalogue.Currency,
            warehouseCode,
            DateTime.UtcNow,
            ComputeETag(catalogue.PriceListNumber, items),
            items);
    }

    /// <summary>
    /// The depot whose stock the bands describe: the one the shop's van loads from.
    /// </summary>
    /// <remarks>
    /// Reached the same way the route is — through the customer's assigned business partner to the
    /// van's user account — because the business partner is the van. Not the van's own warehouse:
    /// that holds what is on the truck right now, which says nothing about what can be loaded for a
    /// call two days from now.
    /// </remarks>
    private async Task<string?> ResolveStockWarehouseAsync(
        string businessPartnerCode,
        CancellationToken cancellationToken)
    {
        return await context.Users
            .AsNoTracking()
            .Where(u => u.AssignedBusinessPartnerCode == businessPartnerCode
                        && u.SupplyingWarehouseCode != null
                        && u.SupplyingWarehouseCode != "")
            .Select(u => u.SupplyingWarehouseCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Available quantity per item in the most recent snapshot for the warehouse.
    /// </summary>
    /// <remarks>
    /// Batches are summed: a customer ordering a case does not care which batch fills it, and the
    /// snapshot holds a row per batch.
    /// </remarks>
    private async Task<Dictionary<string, decimal>> ReadAvailableQuantitiesAsync(
        string? warehouseCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        var latestSnapshotId = await context.DailyStockSnapshots
            .AsNoTracking()
            .Where(s => s.WarehouseCode == warehouseCode)
            .OrderByDescending(s => s.SnapshotDate)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSnapshotId is null)
        {
            logger.LogInformation(
                "No stock snapshot for warehouse {Warehouse}; van sales customer catalogue availability will read as unknown.",
                warehouseCode);
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(i => i.SnapshotId == latestSnapshotId)
            .GroupBy(i => i.ItemCode)
            .Select(g => new { ItemCode = g.Key, Available = g.Sum(i => i.AvailableQuantity) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.ItemCode, r => r.Available, StringComparer.OrdinalIgnoreCase);
    }

    private static VanSalesStockBand BandFor(
        string itemCode,
        string? warehouseCode,
        IReadOnlyDictionary<string, decimal> available,
        decimal lowThreshold)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode) || available.Count == 0)
        {
            // Nothing to say. Better than claiming stock we have not looked at.
            return VanSalesStockBand.Unknown;
        }

        if (!available.TryGetValue(itemCode, out var quantity))
        {
            // The snapshot covers this warehouse but not this item, which means none was counted.
            return VanSalesStockBand.OutOfStock;
        }

        if (quantity <= 0)
        {
            return VanSalesStockBand.OutOfStock;
        }

        return quantity <= lowThreshold ? VanSalesStockBand.Low : VanSalesStockBand.InStock;
    }

    /// <summary>
    /// A hash of what the customer would see, so an unchanged catalogue costs a 304 and no payload.
    /// </summary>
    /// <remarks>
    /// Covers price, tax and availability as well as the item list, because a band moving from
    /// in-stock to out is exactly the change a handset must not miss. Deliberately excludes the
    /// generation timestamp, which would make every response a fresh tag and the whole mechanism
    /// pointless.
    /// </remarks>
    private static string ComputeETag(int priceListNumber, IReadOnlyList<VanSalesCatalogueItem> items)
    {
        var builder = new StringBuilder();
        builder.Append(priceListNumber).Append('|');

        foreach (var item in items)
        {
            builder
                .Append(item.ItemCode).Append(';')
                .Append(item.ItemName).Append(';')
                .Append(item.UnitPrice.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(item.TaxPercent.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append((int)item.Availability).Append('|');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..32];
    }
}
