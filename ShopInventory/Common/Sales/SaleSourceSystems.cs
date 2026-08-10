namespace ShopInventory.Common.Sales;

/// <summary>
/// The <c>SourceSystem</c> values stored on a <c>DesktopSaleEntity</c>.
///
/// This is not cosmetic. The two sources reach SAP by different routes and must never both claim the
/// same sale: a desktop sale is folded into one consolidated invoice per customer at end of day, while a
/// van sale posts one-to-one so that each SAP invoice still maps to exactly one ZIMRA receipt. Both are
/// already fiscalised before they get here, so a sale posted twice is fiscalised once but invoiced
/// twice, and the only way back is a manual credit note.
/// </summary>
public static class SaleSourceSystems
{
    /// <summary>
    /// A sale captured and ZIMRA-stamped on a van handset, uploaded during the day and posted to SAP
    /// one-to-one by <c>VanSalesEndOfDayPostingService</c>.
    /// </summary>
    public const string VanSales = "KefalosVanSales";

    /// <summary>
    /// Van sales are excluded from <c>ConsolidateDailySales</c> by this test. They sit in the same table
    /// with the same <c>Pending</c> status that the consolidation handler selects on, so without it the
    /// 18:00 consolidation would sweep up a van sale that the van posting job is also about to post.
    /// </summary>
    public static bool IsVanSale(string? sourceSystem) =>
        string.Equals(sourceSystem?.Trim(), VanSales, StringComparison.OrdinalIgnoreCase);
}
