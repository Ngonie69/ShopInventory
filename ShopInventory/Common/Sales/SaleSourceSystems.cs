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

    /// <summary>
    /// A shop till sale, fiscalised the moment it is rung up so the customer's receipt can print, and
    /// posted to SAP one-to-one by <c>DesktopSalePostingService</c>.
    /// </summary>
    public const string ShopTill = "KefalosShopTill";

    /// <summary>
    /// A cart-vendor invoice. Same route to SAP as <see cref="ShopTill"/>; it differs in taking cash
    /// only, naming a vendor, printing nothing, and fiscalising in the background rather than inline.
    /// </summary>
    public const string Vending = "KefalosVending";

    /// <summary>
    /// The sources whose sales reach SAP as one invoice each, and which the 18:00 consolidation must
    /// therefore leave alone.
    /// </summary>
    /// <remarks>
    /// Adding a source here without also giving it a posting route strands its sales unposted; adding a
    /// posting route without listing it here invoices them twice. The two go together.
    /// </remarks>
    public static readonly string[] PostedPerSale = [VanSales, ShopTill, Vending];

    /// <summary>The sources a till may declare on <c>POST /api/DesktopIntegration/sales</c>.</summary>
    public static readonly string[] TillSources = [ShopTill, Vending];

    /// <summary>
    /// The sources <c>DesktopSalePostingService</c> claims. Van sales are posted by their own service
    /// and must not appear here.
    /// </summary>
    public static readonly string[] PostedByDesktopSaleJob = [ShopTill, Vending];

    public static bool IsSupportedTillSource(string? sourceSystem) =>
        TillSources.Any(source => string.Equals(sourceSystem?.Trim(), source, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a sale created before this route existed identifies itself as. Consolidated at 18:00, as
    /// it always has been.
    /// </summary>
    public const string LegacyDesktop = "DESKTOP_APP";

    /// <summary>
    /// Canonicalises what a till declared.
    /// </summary>
    /// <remarks>
    /// Casing matters here in a way it does not for most strings: the routing test between the posting
    /// service and the 18:00 consolidation is an equality check against these constants, so a till that
    /// spelled its source differently would be picked up by neither and its sale would never reach SAP.
    ///
    /// A caller that declares nothing gets <see cref="LegacyDesktop"/>, not <see cref="ShopTill"/>.
    /// That is the whole point: declaring nothing must keep meaning what it meant before, because the
    /// alternative is silently moving every existing caller onto a posting route and, if that route is
    /// not running yet, stranding its sales — fiscalised, refused by the consolidation, claimed by
    /// nobody, with no error anywhere. A till opts in by naming itself.
    ///
    /// Anything else unrecognised is likewise returned untouched.
    /// </remarks>
    public static string NormalizeTillSource(string? sourceSystem)
    {
        if (string.IsNullOrWhiteSpace(sourceSystem))
        {
            return LegacyDesktop;
        }

        var trimmed = sourceSystem.Trim();

        return TillSources.FirstOrDefault(
            source => string.Equals(trimmed, source, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
    }
}
