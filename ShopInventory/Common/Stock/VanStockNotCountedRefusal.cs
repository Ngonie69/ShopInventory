using ShopInventory.Services;

namespace ShopInventory.Common.Stock;

/// <summary>
/// What a rep is told when a van's sale cannot be checked: SAP gave no figure for the van, and this
/// system has none of its own for the day.
/// </summary>
/// <remarks>
/// <para><b>Why it is refused and not let through.</b> The check exists so that SAP does not refuse to
/// go negative after the office has signed the receipt. With no figure from either side there is
/// nothing to check against, and the sale is refused before the device is asked.</para>
///
/// <para><b>Why not yesterday's figure.</b> Yesterday's opening stock, plus loads, less the sales since,
/// is not what SAP holds this morning. Goods issues for breakages and posted stock counts move a van
/// with no sale or transfer behind them; and the loads and returns that do arrive as transfer
/// adjustments are never compared against SAP for a van, the way the hourly reconciliation compares a
/// shop's, so one the listener missed stays missed. A figure that reads high is a signed receipt SAP
/// then refuses; one that reads low refuses, with a number, stock the van is holding.</para>
///
/// <para><b>What the text may say.</b> The handset shows it as it stands, behind "Error:", after
/// passing it through <c>StockValidationHelper.ParseSapError</c>, which replaces any message holding
/// one of its keywords with a canned one: "timed out" becomes "the server took too long", a
/// connection word becomes "your order may or may not have been submitted", and "insufficient" offers
/// to edit the basket. <c>VanStockNotCountedRefusalTests</c> runs every wording here through a copy
/// of that helper.</para>
///
/// <para><b>What it must not say.</b> That the rep can send a count. <c>ReportVanSalesStockPositionHandler</c>
/// has written nothing since 2026-09-29, so nothing done on the handset gives this system a figure.
/// Until 2026-10-11 this told the rep to open Start the day or sync, which changed nothing however
/// often it was done.</para>
/// </remarks>
public static class VanStockNotCountedRefusal
{
    /// <summary>
    /// The one-line reason for the whole refusal, in place of "insufficient stock": the stock was not
    /// found short, it was not checked.
    /// </summary>
    public const string Summary = "Van stock could not be checked";

    /// <param name="warehouseCode">The van's warehouse.</param>
    /// <param name="sapHeldBack">
    /// True when SAP was not asked at all — switched off, a declared outage or an open circuit. False
    /// when it was asked for this very sale and did not answer.
    /// </param>
    /// <param name="utcNow">The current instant, in UTC.</param>
    /// <param name="fetchTimeCat">When the morning read of stock from SAP runs, in CAT.</param>
    public static (string Message, string SuggestedAction) Describe(
        string warehouseCode,
        bool sapHeldBack,
        DateTime utcNow,
        TimeSpan fetchTimeCat)
    {
        var cause = sapHeldBack
            ? "SAP is not available right now"
            : "SAP did not answer when asked for the van's stock";

        // Before the fetch time the ledger day is still yesterday, and no read for today was ever due.
        // After it, the read was due and has not finished: still running, failed, or never started.
        var readWasDue = StockLedgerDay.Resolve(utcNow, fetchTimeCat) == AuditService.ToCAT(utcNow).Date;
        var fetchTime = fetchTimeCat.ToString(@"hh\:mm");

        var gap = readWasDue
            ? $"this morning's {fetchTime} stock read has not finished for van {warehouseCode}"
            : $"this system has no figure of its own for van {warehouseCode} before the {fetchTime} stock read";

        var action = readWasDue
            ? "Try the sale again in a few minutes; if it is still refused, tell the office so the stock "
              + "read can be run again for the van"
            : "Try the sale again in a few minutes, and tell the office if it is still refused";

        return (
            $"{cause}, and {gap}, so the sale cannot be checked. Nothing on the handset changes this. {action}.",
            action);
    }
}
