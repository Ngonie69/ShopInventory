using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The lines of business a desktop sale belongs to: shop counters, vending and vans.
/// </summary>
/// <remarks>
/// <para>
/// They sell different ranges to different buyers in different ways — a counter sells single units to
/// walk-in customers through the day, a vending settlement is a vendor's whole round paid in at once — so
/// a review that adds them together describes neither. Reports that read them as one business ask for
/// each by its key instead.
/// </para>
/// <para>
/// Decided by the sale's source system. Everything that is not vending or a van is a shop, including
/// the legacy desktop app's sales and any sale with no source recorded.
/// </para>
/// </remarks>
public static class SaleBusinesses
{
    public const string Shops = "shops";

    public const string Vending = "vending";

    public const string Vans = "vans";

    /// <summary>Every business, in the order a review presents them.</summary>
    public static readonly string[] All = [Shops, Vending, Vans];

    /// <summary>
    /// The businesses a counter report covers, in the order a review presents them. The vans are reported
    /// under van sales, from <c>VanSalesFactReader</c>.
    /// </summary>
    public static readonly string[] Counters = [Shops, Vending];

    public static bool IsKnown(string? business) =>
        All.Contains(business?.Trim().ToLowerInvariant());

    /// <summary>The business a sale from this source belongs to.</summary>
    public static string Of(string? sourceSystem)
    {
        var source = sourceSystem?.Trim();
        if (string.Equals(source, SaleSourceSystems.Vending, StringComparison.OrdinalIgnoreCase))
        {
            return Vending;
        }

        return SaleSourceSystems.IsVanSale(source) ? Vans : Shops;
    }

    public static string Label(string business) => business switch
    {
        Vending => "Vending",
        Vans => "Van sales",
        _ => "Shops"
    };

    /// <summary>The sales of one business; a null or unknown key leaves the query as it is.</summary>
    public static IQueryable<DesktopSaleEntity> InBusiness(this IQueryable<DesktopSaleEntity> sales, string? business) =>
        business?.Trim().ToLowerInvariant() switch
        {
            Vending => sales.Where(s => s.SourceSystem == SaleSourceSystems.Vending),
            // Both routes a van sale arrives by: this table holds each of them once, the offline upload as
            // the sale and the online one as its receipt. Nothing in a counter report asks for this.
            Vans => sales.Where(s => s.SourceSystem == SaleSourceSystems.VanSales
                || s.SourceSystem == SaleSourceSystems.VanSalesOnline),
            Shops => sales.Where(s => s.SourceSystem == null
                || (s.SourceSystem != SaleSourceSystems.Vending
                    && s.SourceSystem != SaleSourceSystems.VanSales
                    && s.SourceSystem != SaleSourceSystems.VanSalesOnline)),
            _ => sales
        };

    /// <summary>
    /// The sales rung up at a counter — shops and vending together — and nothing off a van, by either route.
    /// </summary>
    /// <remarks>
    /// What a report about the counters reads when it is asked for no one business. The vans are not a third
    /// kind of counter: half their sales are in <c>StockReservations</c> and never reach this table as a sale,
    /// so they are read whole by <c>VanSalesFactReader</c> and reported under van sales instead.
    /// </remarks>
    public static IQueryable<DesktopSaleEntity> AtCounters(this IQueryable<DesktopSaleEntity> sales) =>
        sales.Where(s => s.SourceSystem == null
            || (s.SourceSystem != SaleSourceSystems.VanSales
                && s.SourceSystem != SaleSourceSystems.VanSalesOnline));
}
