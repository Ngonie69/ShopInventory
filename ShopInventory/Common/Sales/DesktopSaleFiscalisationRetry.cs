using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Which unfiscalised sales may be offered to the fiscal device again, automatically or on request.
/// </summary>
/// <remarks>
/// <para>
/// One rule with three readers: <c>DesktopSaleFiscalisationSweep</c> selects on it, the retry command
/// refuses from it, and the sales list and fiscalisation console describe it. Written separately, those
/// drift, and the direction they drift in is a console that says "retried automatically" about a sale
/// nothing will ever touch again. That is how a shop-till sale that failed at the counter came to sit
/// "Fiscal Failed" indefinitely.
/// </para>
/// <para>
/// <b>Every retry asks the device first.</b> A retry is only safe because the sale's fiscal invoice number
/// is its external reference, stable across attempts, and the device is asked whether it already holds a
/// receipt under that number before anything is submitted. If it cannot be asked, nothing is sent.
/// </para>
/// </remarks>
public static class DesktopSaleFiscalisationRetry
{
    /// <summary>
    /// How long a shop till's own request may still be fiscalising a sale it committed as Pending.
    /// </summary>
    /// <remarks>
    /// A till sale is saved Pending and then fiscalised inline, in the request, so a Pending till sale is
    /// normally a request still waiting on the device. Offering it again inside that window would put a
    /// second submission in flight beside the first. Well past any client timeout, a Pending till sale is
    /// a request that died — the process restarted, the save failed — and nothing else will finish it.
    /// </remarks>
    public static readonly TimeSpan InlineRequestWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The sources whose sales are fiscalised on this side and may therefore be retried here.
    /// </summary>
    /// <remarks>
    /// Van sales depend on who signs. Under the in-house platform the handset signs for itself and its
    /// receipts are handed on by <c>VanSalesSignedReceiptIngestService</c>, so fiscalising one here would
    /// file a second receipt for a sale that already has one. Under REVMax nothing in the van can sign, so
    /// an offline van sale arrives unstamped and is fiscalised here or not at all.
    /// <see cref="SaleSourceSystems.VanSalesOnline"/> is never here: those rows carry a receipt for an
    /// invoice already fiscalised in the request that made it.
    /// </remarks>
    public static string[] RetriedSources(bool usesPlatform) => usesPlatform
        ? [SaleSourceSystems.Vending, SaleSourceSystems.ShopTill]
        : [SaleSourceSystems.Vending, SaleSourceSystems.ShopTill, SaleSourceSystems.VanSales];

    /// <summary>
    /// Whether an earlier attempt may already have reached the device, so it must be asked before sending.
    /// </summary>
    /// <remarks>
    /// A shop till sale always counts. Its inline attempt increments the counter in memory and a request
    /// that died before saving leaves zero on the row, so the counter cannot be trusted to say no.
    /// </remarks>
    public static bool MayAlreadyBeFiled(DesktopSaleEntity sale) =>
        sale.FiscalizationAttempts > 0 ||
        string.Equals(sale.SourceSystem, SaleSourceSystems.ShopTill, StringComparison.Ordinal);

    /// <summary>
    /// Whether a shop till's own request may still be fiscalising this sale.
    /// </summary>
    public static bool IsInlineRequestInFlight(
        string? sourceSystem,
        DesktopSaleFiscalizationStatus status,
        DateTime createdAtUtc,
        DateTime nowUtc) =>
        status == DesktopSaleFiscalizationStatus.Pending &&
        string.Equals(sourceSystem, SaleSourceSystems.ShopTill, StringComparison.Ordinal) &&
        createdAtUtc > nowUtc - InlineRequestWindow;

    /// <summary>
    /// Why this sale may not be fiscalised again on request, or null when it may.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attempt budget and the lookback window are not part of this. They ration the automatic
    /// retries; a person pressing Retry has overruled them, usually having just fixed what the device was
    /// refusing.
    /// </para>
    /// <para>
    /// A sale marked for reconciliation may be retried under REVMax and not under the platform. REVMax's
    /// <c>GetInvoice</c> reads the device itself, so asking it first is exactly the look-up reconciliation
    /// calls for. The platform's receipt check reads an archive that can lag a submission it has not
    /// finished, so there a person still has to look.
    /// </para>
    /// </remarks>
    public static string? ManualRefusal(
        string? sourceSystem,
        DesktopSaleFiscalizationStatus status,
        bool requiresReconciliation,
        DateTime createdAtUtc,
        DateTime nowUtc,
        bool usesPlatform)
    {
        switch (status)
        {
            case DesktopSaleFiscalizationStatus.Success:
                return "This sale is already fiscalised.";
            case DesktopSaleFiscalizationStatus.Skipped:
                return "Fiscalisation was not asked for when this sale was made.";
        }

        if (sourceSystem is null || !RetriedSources(usesPlatform).Contains(sourceSystem))
        {
            return string.Equals(sourceSystem, SaleSourceSystems.VanSales, StringComparison.Ordinal)
                ? "This van sale is signed on its handset, not here."
                : "This sale is not fiscalised by this system, so there is nothing to retry here.";
        }

        if (IsInlineRequestInFlight(sourceSystem, status, createdAtUtc, nowUtc))
        {
            return "The till is still fiscalising this sale. Give it a few minutes.";
        }

        if (requiresReconciliation && usesPlatform)
        {
            return "The platform could not say whether this receipt was signed. Look it up on the "
                + "fiscalisation console before retrying.";
        }

        return null;
    }
}
