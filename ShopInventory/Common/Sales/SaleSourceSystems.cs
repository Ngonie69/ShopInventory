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
    /// The ZIMRA receipt a van handset signed for a sale it made <i>online</i>, where the invoice went
    /// straight to SAP through the reservation and the sale is already represented by that reservation.
    /// </summary>
    /// <remarks>
    /// A distinct source rather than a flag on <see cref="VanSales"/>, and the distinction is the whole
    /// reason the row is safe to write at all.
    ///
    /// <para>
    /// An online van sale already exists twice over: as the confirmed <c>StockReservation</c> the report
    /// stream counts, and as the SAP invoice that reservation posted. The only thing missing from it is
    /// the handset's signed receipt, which has nowhere else to live — so a <c>DesktopSaleEntity</c> is
    /// written to carry it to <c>VanSalesSignedReceiptIngestService</c> and no further. Under
    /// <see cref="VanSales"/> that same row would be counted a second time by
    /// <c>VanSalesFactReader</c>, which unions van <c>DesktopSales</c> with confirmed reservations, and
    /// posted a second time by <c>VanSalesEndOfDayPostingService</c>, which would put a duplicate
    /// invoice in SAP for a sale SAP already has.
    /// </para>
    ///
    /// <para>
    /// So the rule for every reader is one question: does it want <i>the sale</i>, or does it want
    /// <i>the receipt</i>? Anything counting money or posting documents must leave these rows alone —
    /// the reservation speaks for them. Anything about the fiscal chain must include them, because the
    /// receipt is real, its number came off a device's sequence, and FDMS will not close that device's
    /// fiscal day without it. The same question applies to any report that unions <c>DesktopSales</c>
    /// with <c>StockReservations</c>: the route customer reports do, and exclude this source by name.
    /// </para>
    /// </remarks>
    public const string VanSalesOnline = "KefalosVanSalesOnline";

    /// <summary>
    /// Both routes a sale off a van handset can arrive by. What the fiscal side reads: one handset owns
    /// one ZIMRA device, and its receipts are one chain whether the van had signal at the time or not.
    /// </summary>
    public static readonly string[] VanSaleSources = [VanSales, VanSalesOnline];

    /// <summary>
    /// Whether this sale came off a van handset, by either route.
    /// </summary>
    /// <remarks>
    /// Van sales are excluded from <c>ConsolidateDailySales</c> by <see cref="PostedPerSale"/>. They sit
    /// in the same table with the same <c>Pending</c> status that the consolidation handler selects on,
    /// so without it the 18:00 consolidation would sweep up a van sale that the van posting job is also
    /// about to post.
    ///
    /// This answers the fiscal question instead — "is the hand-over drain the owner of this row's
    /// receipt" — and so covers both van sources. It is not a test for "should this be posted": an
    /// online van sale is already in SAP, and <see cref="VanSales"/> alone is what the posting route
    /// selects on.
    /// </remarks>
    public static bool IsVanSale(string? sourceSystem) =>
        VanSaleSources.Any(source =>
            string.Equals(sourceSystem?.Trim(), source, StringComparison.OrdinalIgnoreCase));

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
    ///
    /// <see cref="VanSalesOnline"/> is the one member with no posting route of its own, and it is here
    /// for the same reason the others are: to be left alone at 18:00. Its invoice reached SAP in the
    /// request that made the sale, so there is nothing left to post and a consolidation that swept the
    /// row up would invoice the sale a second time.
    /// </remarks>
    public static readonly string[] PostedPerSale = [VanSales, VanSalesOnline, ShopTill, Vending];

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
    /// Whether this source's sales are fiscalised by the background sweep rather than in the request.
    /// </summary>
    /// <remarks>
    /// A shop till fiscalises inline because the receipt has to print before the customer walks away,
    /// so the request cannot return until the platform has signed it. Vending prints nothing and has
    /// no one waiting, so holding the request open buys nothing and costs the operator the wait.
    /// </remarks>
    public static bool FiscalisesInBackground(string? sourceSystem) =>
        string.Equals(sourceSystem?.Trim(), Vending, StringComparison.OrdinalIgnoreCase);

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
