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
        sale.FiscalizationRequiresReconciliation ||
        string.Equals(sale.SourceSystem, SaleSourceSystems.ShopTill, StringComparison.Ordinal);

    /// <summary>
    /// Whether a sale marked for reconciliation may be offered again, by the sweep or on request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under REVMax, yes. The mark means an attempt ended with no answer from the device, and the device
    /// is the thing that can answer: <c>GetInvoice</c> reads the device itself, so asking it first is
    /// exactly the look-up reconciliation calls for. If it cannot be asked, or does not say plainly that
    /// it holds nothing, nothing is sent.
    /// </para>
    /// <para>
    /// The sweep used to leave every marked sale for a person, and an outage marks every sale it
    /// touches, including ones refused at the connection, which never reached the device. When the device
    /// came back, nothing fiscalised them, and every sale made during the outage sat unfiscalised and
    /// uninvoiced until someone pressed Retry on each one.
    /// </para>
    /// <para>
    /// Under the platform, no. Its receipt check reads an archive that can lag a submission it has not
    /// finished, so a person still has to look.
    /// </para>
    /// </remarks>
    public static bool MayRetryReconciliation(bool usesPlatform) => !usesPlatform;

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
        if (status == DesktopSaleFiscalizationStatus.Success)
        {
            return "This sale is already fiscalised.";
        }

        // Skipped is retryable, and used not to be — it was refused with "fiscalisation was not asked
        // for when this sale was made", which was true and was not a reason. It became a reason the
        // moment the posting services stopped admitting a Skipped sale to SAP: the sale then has no
        // receipt, no invoice and no way to acquire either, and refusing the one control that could
        // still fix it strands the money permanently.
        //
        // Safe for the same reason every retry here is safe — the device is asked whether it already
        // holds a receipt under this sale's reference before anything is submitted, and if it cannot
        // be asked, nothing is sent. Whether fiscalisation was originally asked for changes nothing
        // about that question.

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

        if (requiresReconciliation && !MayRetryReconciliation(usesPlatform))
        {
            return "The platform could not say whether this receipt was signed. Look it up on the "
                + "fiscalisation console before retrying.";
        }

        return null;
    }
}
