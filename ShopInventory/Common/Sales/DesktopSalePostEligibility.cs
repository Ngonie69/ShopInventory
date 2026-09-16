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
/// fiscal clause matches <c>DesktopSalePostingService</c>'s pending query and
/// <c>VanSalesEndOfDayPostingService</c>'s, which now ask the same question of both routes — see below
/// for the two disagreements that used to be here and why neither survived.
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

        // One rule now, where the two routes used to disagree.
        //
        // Both halves of that disagreement have gone, and each for its own reason.
        //
        // <b>Skipped used to post.</b> It is what a sale got when the caller sent `fiscalize: false`,
        // and the argument for letting it through was that it will never become Success, so holding it
        // would strand it. What that bought was an A/R invoice in SAP for goods ZIMRA was never told
        // about, indistinguishable in the ledger from a fiscalised one, on nothing but a flag in a
        // request body. The flag is now refused outright for till and vending sales, so no new row can
        // reach here Skipped, and the ones that already have are held rather than posted — retryable,
        // because DesktopSaleFiscalisationRetry now offers a Skipped sale to the device.
        //
        // <b>A van sale used to post unfiscalised.</b> The reasoning was that the handset stamped it
        // hours ago or never will, the money is real, and the fiscal side is chased separately through
        // ReceiptIngestStatus. The half of that which does not hold is "stamped hours ago": a stamped
        // sale is written Success, so Failed here means the handset stamped <i>nothing</i> — and under
        // REVMax, which is the live provider, no handset can stamp at all (GetVanSalesFiscalLeaseHandler
        // hands out an office-fiscalised lease), so every van sale is fiscalised on this side by
        // DesktopSaleFiscalisationSweep exactly as a till sale is. Failed is therefore the same thing it
        // is on a till: a sale whose receipt has not been signed yet, which the sweep will normally sign
        // within the minute, and posting it meanwhile invoices a sale ZIMRA has no receipt for. It
        // matters more here than on a till, not less: a van sale posts one-to-one, and its invoice is
        // precisely what the SAP-to-FDMS reconciliation goes looking for a receipt against.
        if (fiscalizationStatus != DesktopSaleFiscalizationStatus.Success)
        {
            return fiscalizationStatus switch
            {
                DesktopSaleFiscalizationStatus.Pending =>
                    "This sale has not been fiscalised yet. Posting it now would invoice a sale that has no receipt.",
                DesktopSaleFiscalizationStatus.Skipped =>
                    "This sale was created with fiscalisation switched off, so it has no receipt and cannot be "
                    + "invoiced. Retry fiscalisation to sign it now.",
                _ =>
                    "This sale's fiscalisation failed, so it cannot be invoiced until the device signs it. "
                    + "Retry fiscalisation to try again now."
            };
        }

        return null;
    }
}
