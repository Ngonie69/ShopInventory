using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Whether a sale may be posted to SAP on request, and if not, why not.
/// </summary>
/// <remarks>
/// <para>
/// One rule with two readers, which is the whole reason it is here rather than inline in either. The
/// sales list sets <c>CanPostToSap</c> from this so the console knows which rows to offer a button
/// on; the posting command refuses from this so a caller that ignores the console cannot post
/// something the console would not have offered. Written twice, those two drift, and the direction
/// they drift in is a button that posts a sale the route was deliberately keeping out of SAP.
/// </para>
/// <para>
/// <b>It mirrors each route rather than inventing a rule of its own.</b> A manual post must not
/// refuse what the background pass would post, or the button is useless exactly where it is needed;
/// and it must not post what the pass refuses, or the button is a way around a safety rule. So the
/// till clause matches <c>DesktopSalePostingService</c>'s pending query and the van clause matches
/// <c>VanSalesEndOfDayPostingService</c>'s — including their one real disagreement, below.
/// </para>
/// <para>
/// <b>The attempt cap is not part of this.</b> It rations automatic retries; a person pressing Post
/// has overruled it, usually having just fixed what SAP was refusing. Both routes' own on-request
/// methods leave it out for the same reason.
/// </para>
/// </remarks>
public static class DesktopSalePostEligibility
{
    public static bool CanPost(DesktopSaleEntity sale) => Refusal(sale) is null;

    /// <summary>
    /// Why this sale may not be posted, in words an operator can act on, or null when it may be.
    /// </summary>
    public static string? Refusal(DesktopSaleEntity sale)
        => Refusal(sale.SourceSystem, sale.ConsolidationStatus, sale.FiscalizationStatus);

    /// <summary>
    /// The same rule over loose values, for a caller reading projected columns rather than an entity.
    /// </summary>
    public static string? Refusal(
        string? sourceSystem,
        DesktopSaleConsolidationStatus consolidationStatus,
        DesktopSaleFiscalizationStatus fiscalizationStatus)
    {
        // Asked first, because the answer for a sale SAP already has should say so rather than
        // complaining about the route it took to get there.
        switch (consolidationStatus)
        {
            case DesktopSaleConsolidationStatus.Consolidated:
                return "This sale is already in SAP.";
            case DesktopSaleConsolidationStatus.Excluded:
                return "This sale was excluded from posting. Clear the exclusion before posting it.";
        }

        var isTillSale = sourceSystem is not null && SaleSourceSystems.PostedByDesktopSaleJob.Contains(sourceSystem);
        var isVanSale = string.Equals(sourceSystem, SaleSourceSystems.VanSales, StringComparison.Ordinal);

        if (!isTillSale && !isVanSale)
        {
            // The two remaining sources are not refusals of this sale so much as statements that
            // something else owns it, so each says which.
            return string.Equals(sourceSystem, SaleSourceSystems.VanSalesOnline, StringComparison.Ordinal)
                ? "This row carries the receipt for a sale that reached SAP through its reservation, so there is nothing to post."
                : "This sale reaches SAP through the end-of-day consolidation, not as an invoice of its own.";
        }

        // The one place the two routes genuinely disagree, and both are right.
        //
        // A till sale is fiscalised on this side, so an unfiscalised one may still get its receipt and
        // posting it now would put an invoice in SAP for a sale ZIMRA has no receipt for. Skipped is
        // not the same thing — it is what a sale gets when fiscalisation was not asked for — so it
        // posts, as it always has.
        //
        // A van sale was stamped on the handset hours ago or never will be: `Failed` here means the
        // upload carried no usable signature, and the van route posts it anyway because the money is
        // real and the fiscal side is chased separately through ReceiptIngestStatus. Refusing it would
        // strand takings the automatic pass posts every night.
        if (isTillSale &&
            fiscalizationStatus is not (DesktopSaleFiscalizationStatus.Success or DesktopSaleFiscalizationStatus.Skipped))
        {
            return fiscalizationStatus == DesktopSaleFiscalizationStatus.Pending
                ? "This sale has not been fiscalised yet. Posting it now would invoice a sale that has no receipt."
                : "This sale's fiscalisation failed, so it needs a person before it can be invoiced.";
        }

        return null;
    }
}
