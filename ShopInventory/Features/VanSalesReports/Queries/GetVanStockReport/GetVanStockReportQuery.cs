using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;

/// <summary>
/// Does each van's stock in SAP move only by documents we can name, and did every sale a van made
/// reach SAP before the next morning's count?
/// </summary>
/// <remarks>
/// Built on the morning stock snapshot, which is SAP's own book stock for the van read at about
/// 07:00 — <c>OriginalQuantity</c>, not the running <c>AvailableQuantity</c>, which no van path moves.
///
/// <b>The count is SAP's, so SAP's documents explain it.</b> SAP's stock moves only when a document
/// is posted, so the change between two counts is exactly what SAP <em>created</em> between the two
/// reads: invoices, credit notes, stock transfers, and postings this report does not read (goods
/// issues, receipts, stock counts). An earlier version explained the change with the app's own sales
/// grouped by trading day, and every sale that reached SAP a day or four late — the daily van invoice
/// posted the next morning, the reposts after the 2026-09-23 rollback — showed up as stock missing one
/// morning and found the next. Matching by creation time also lets a morning be compared across a
/// missing count: the documents cover the gap, so nothing is attributed to the wrong day.
///
/// <b>Sales are checked separately, by trading day.</b> What the van recorded is compared with the
/// SAP invoices dated that day, and with when SAP created them, so a day that posted late or not at
/// all is named as such rather than hidden inside the stock figures.
///
/// <b>These are book figures.</b> A van that is physically short shows only once someone counts it
/// and posts the difference in SAP.
/// </remarks>
public sealed record GetVanStockReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    string? VanWarehouseCode = null,
    int DeadStockDays = 14
) : IRequest<ErrorOr<VanStockReportResult>>;

public sealed record VanStockReportResult(
    DateTime FromDate,
    DateTime ToDate,
    int DeadStockDays,
    VanStockSummaryResult Summary,
    List<VanStockDayResult> Days,
    List<VanStockMorningResult> Mornings,
    List<VanStockSalesDayResult> SalesDays,
    List<VanStockItemResult> Items,
    List<VanStockExpiryResult> Expiring,
    VanStockQualityResult Quality,
    List<VanStockVanResult>? Vans = null
);

// ── Per van ─────────────────────────────────────────────────────────────────────

/// <summary>
/// One van across the period, in counts rather than quantities: a van carries cases, kilograms and
/// singles, and a sum across them is a figure in no unit.
/// </summary>
/// <param name="AccountCodes">The business partners this van's sales invoice to in SAP.</param>
/// <param name="DaysCounted">Mornings with a snapshot.</param>
/// <param name="DaysWithSales">Of those, mornings whose trading day recorded at least one sale.</param>
/// <param name="ItemDays">Item-mornings carried — each item counted once per morning it was on the van.</param>
/// <param name="SoldItemDays">Of those, item-mornings on which the item sold.</param>
/// <param name="DeadItemCount">Items carried the dead-stock threshold or longer on this van and never sold from it.</param>
/// <param name="MorningsChecked">Mornings compared with the count before them against SAP's documents.</param>
/// <param name="MorningsTied">Of those, mornings every item of which SAP's documents explain.</param>
/// <param name="SalesDaysLate">Trading days whose sales were all invoiced, but after the next morning's count.</param>
/// <param name="SalesDaysNotInSap">Trading days with recorded sales that have no SAP invoice yet.</param>
/// <param name="SalesDaysSapOnly">Trading days with SAP invoices from this van and no sale recorded by the app.</param>
/// <param name="MaxDaysLate">The longest a day's sales took to reach SAP, in days after the trading day.</param>
public sealed record VanStockVanResult(
    string VanWarehouseCode,
    int DaysCounted,
    int DaysWithSales,
    int ItemCount,
    int ItemDays,
    int SoldItemDays,
    int DeadItemCount,
    List<string>? AccountCodes = null,
    int MorningsChecked = 0,
    int MorningsTied = 0,
    int SalesDays = 0,
    int SalesDaysOnTime = 0,
    int SalesDaysLate = 0,
    int SalesDaysNotInSap = 0,
    int SalesDaysSapOnly = 0,
    int? MaxDaysLate = null);

// ── Summary ─────────────────────────────────────────────────────────────────────

public sealed record VanStockSummaryResult(
    int VanCount,
    int SnapshotDayCount,
    int MissingSnapshotDays,
    int ItemCount,
    int DeadItemCount,
    decimal LoadedQuantity,
    decimal SoldQuantity,
    DateTime? LatestSnapshotDate,
    int? SnapshotAgeDays,
    bool SapChecked = false,
    DateTime? LatestCountAt = null)
{
    /// <summary>
    /// What share of the load sold. Null when nothing was loaded — a van with no stock has no
    /// sell-through, and 0% would read as a van that failed to sell what it had.
    /// </summary>
    public double? SellThroughRate =>
        LoadedQuantity > 0 ? (double)(SoldQuantity / LoadedQuantity) : null;

    /// <summary>
    /// True when the newest snapshot is not from today. Every figure here is then describing a van
    /// as it stood on an older morning, and the page has to say so.
    /// </summary>
    public bool IsStale => SnapshotAgeDays is > 0;
}

// ── C1: load, sold, remaining ───────────────────────────────────────────────────

/// <summary>
/// One van on one day: what it opened with, what sold, and what should have been left.
/// </summary>
/// <remarks>
/// <c>ExpectedRemaining</c> is an arithmetic result, not an observation — nothing measures a van at
/// close of business. It is the load less what sold, plus any transfer detected during the day.
/// </remarks>
public sealed record VanStockDayResult(
    string VanWarehouseCode,
    DateTime SnapshotDate,
    bool SnapshotComplete,
    int ItemCount,
    decimal LoadedQuantity,
    decimal SoldQuantity,
    decimal AdjustmentQuantity,
    int SoldItemCount,
    int UnsoldItemCount)
{
    public decimal ExpectedRemaining => LoadedQuantity - SoldQuantity + AdjustmentQuantity;

    public double? SellThroughRate =>
        LoadedQuantity > 0 ? (double)(SoldQuantity / LoadedQuantity) : null;

    /// <summary>
    /// Sold more than was loaded. Not impossible and not necessarily wrong — stock can reach a van
    /// mid-round — but it means the load did not cover the day and is worth seeing.
    /// </summary>
    public bool SoldBeyondLoad => SoldQuantity > LoadedQuantity + AdjustmentQuantity;
}

// ── C2: each morning against SAP's documents ────────────────────────────────────

/// <summary>
/// One van between two counts: what SAP posted in between, and whether it accounts for the change.
/// </summary>
/// <param name="CountedFrom">When the first count read SAP, on the CAT clock.</param>
/// <param name="CountedTo">When the second count read SAP.</param>
/// <param name="GapDays">Days between the two counts; 1 when they are consecutive.</param>
/// <param name="SapChecked">False when SAP could not be read — nothing is then claimed either way.</param>
/// <param name="ItemCount">Items on either count, or moved by a document in between.</param>
/// <param name="ItemsMoved">Items a SAP document moved in between.</param>
/// <param name="ItemsUnexplained">Items whose change no invoice, credit note or transfer accounts for.</param>
/// <param name="Documents">Every SAP document created in between, one row per document.</param>
/// <param name="Unexplained">Those items, largest difference first.</param>
public sealed record VanStockMorningResult(
    string VanWarehouseCode,
    DateTime FromSnapshot,
    DateTime ToSnapshot,
    DateTime CountedFrom,
    DateTime CountedTo,
    int GapDays,
    bool SapChecked,
    int ItemCount,
    int ItemsMoved,
    int ItemsUnexplained,
    List<VanStockDocumentResult> Documents,
    List<VanStockItemMovementResult> Unexplained)
{
    public bool HasGap => GapDays > 1;

    /// <summary>Every item's change is on a document this report read.</summary>
    public bool TiesToSap => SapChecked && ItemsUnexplained == 0;
}

/// <summary>A SAP document as a morning sees it: its printed date beside the moment SAP created it.</summary>
/// <param name="Kind">Invoice, CreditNote, TransferIn or TransferOut.</param>
/// <param name="CreatedTimeKnown">False for a transfer, which SAP stamps with a date only.</param>
/// <param name="ItemCount">Items the document moved on this van.</param>
public sealed record VanStockDocumentResult(
    string Kind,
    int DocEntry,
    int DocNum,
    DateTime DocDate,
    DateTime CreatedAt,
    bool CreatedTimeKnown,
    int ItemCount,
    string? Comments)
{
    /// <summary>Days between the date printed on the document and the day SAP created it.</summary>
    public int DaysBackdated => Math.Max(0, (int)(CreatedAt.Date - DocDate.Date).TotalDays);
}

/// <summary>One item between two counts, in its own inventory unit.</summary>
public sealed record VanStockItemMovementResult(
    string ItemCode,
    string? ItemDescription,
    decimal Opening,
    decimal Invoiced,
    decimal Credited,
    decimal TransferredIn,
    decimal TransferredOut,
    decimal Closing)
{
    public decimal Expected => Opening - Invoiced + Credited + TransferredIn - TransferredOut;

    /// <summary>Found less expected: what SAP moved by a posting this report did not read.</summary>
    public decimal Unexplained => decimal.Round(Closing - Expected, 3);

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

// ── Sales against SAP invoices ──────────────────────────────────────────────────────

/// <summary>
/// One van's trading day: what the van recorded against the SAP invoices dated that day, and when
/// SAP created them.
/// </summary>
/// <param name="Status">
/// OnTime, Late (all invoiced, but after <paramref name="NextCountAt"/>), NotInSap (recorded, not
/// invoiced), Pending (not invoiced yet and the next count has not happened), SapOnly (invoiced in
/// SAP with nothing recorded by the app), or Unchecked when SAP could not be read.
/// </param>
/// <param name="NextCountAt">The count that should already include this day's sales.</param>
/// <param name="DaysLate">Days from the trading day to the last invoice's creation, when Late.</param>
/// <param name="Differences">Items whose recorded and invoiced quantities differ, largest first.</param>
public sealed record VanStockSalesDayResult(
    string VanWarehouseCode,
    DateTime TradingDate,
    string Status,
    int RecordedItems,
    int SapItems,
    int SapInvoiceCount,
    DateTime? FirstInvoicedAt,
    DateTime? LastInvoicedAt,
    DateTime NextCountAt,
    int? DaysLate,
    int ItemsNotInSap,
    int ItemsOnlyInSap,
    List<VanStockSalesItemResult> Differences);

public sealed record VanStockSalesItemResult(
    string ItemCode,
    string? ItemDescription,
    decimal Recorded,
    decimal Invoiced)
{
    public decimal Difference => decimal.Round(Invoiced - Recorded, 3);

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

// ── C3 / C4: sell-through and dead stock ────────────────────────────────────────

/// <summary>
/// One item's life on the vans over the period.
/// </summary>
/// <remarks>
/// <c>DaysOnVanWithoutSelling</c> is the dead-stock measure: how many days this item was loaded and
/// sold nothing. An item riding the round for a fortnight is capital and space that could be
/// carrying something that moves.
/// </remarks>
public sealed record VanStockItemResult(
    string ItemCode,
    string? ItemDescription,
    int VanCount,
    int DaysOnVan,
    int DaysSold,
    int DaysOnVanWithoutSelling,
    decimal LoadedQuantity,
    decimal SoldQuantity,
    DateTime? LastSoldOn)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;

    public double? SellThroughRate =>
        LoadedQuantity > 0 ? (double)(SoldQuantity / LoadedQuantity) : null;

    /// <summary>Carried every day of the period and never sold once.</summary>
    public bool IsDead => DaysSold == 0 && DaysOnVan > 0;
}

// ── C5: expiry exposure ─────────────────────────────────────────────────────────

/// <summary>
/// A batch on a van with a date on it. The one figure here that is about loss rather than efficiency.
/// </summary>
public sealed record VanStockExpiryResult(
    string VanWarehouseCode,
    string ItemCode,
    string? ItemDescription,
    string BatchNumber,
    DateTime ExpiryDate,
    int DaysToExpiry,
    decimal Quantity,
    DateTime SnapshotDate)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;

    public bool HasExpired => DaysToExpiry < 0;
}

// ── Data quality ────────────────────────────────────────────────────────────────

/// <summary>
/// What this report could not see. The snapshot fields lead, because they are the ones that make
/// every other figure describe an older day than the reader thinks.
/// </summary>
public sealed record VanStockQualityResult(
    int MissingSnapshotDays,
    int IncompleteSnapshots,
    int VansWithNoSnapshot,
    int SalesForWarehousesWithNoSnapshot,
    DateTime? LatestSnapshotDate,
    int? SnapshotAgeDays,
    string? SapProblem = null,
    int VansWithNoAccount = 0)
{
    public bool IsClean =>
        MissingSnapshotDays == 0
        && IncompleteSnapshots == 0
        && VansWithNoSnapshot == 0
        && SalesForWarehousesWithNoSnapshot == 0
        && SapProblem is null
        && VansWithNoAccount == 0
        && SnapshotAgeDays is null or 0;

    public IEnumerable<string> Caveats
    {
        get
        {
            if (SapProblem is not null)
            {
                yield return
                    SapProblem + " Nothing on this page says whether a morning ties to SAP or whether a " +
                    "day's sales reached it.";
            }

            if (SnapshotAgeDays is > 0)
            {
                yield return
                    $"The newest stock count is {SnapshotAgeDays:N0} day(s) old" +
                    (LatestSnapshotDate is { } latest ? $" ({latest:dd MMM yyyy})" : "")
                    + ". Every figure here describes the vans as they stood then, not today.";
            }

            if (VansWithNoSnapshot > 0)
            {
                yield return
                    $"{VansWithNoSnapshot:N0} van(s) have no count in this period at all, so nothing " +
                    "can be said about what they carried.";
            }

            if (MissingSnapshotDays > 0)
            {
                yield return
                    $"{MissingSnapshotDays:N0} van-day(s) have no count. The mornings either side are " +
                    "still compared, across the gap, against every SAP document created in between.";
            }

            if (IncompleteSnapshots > 0)
            {
                yield return
                    $"{IncompleteSnapshots:N0} count(s) did not finish, so their item list may be short " +
                    "and the morning after will show items SAP's documents do not explain.";
            }

            if (VansWithNoAccount > 0)
            {
                yield return
                    $"{VansWithNoAccount:N0} van(s) have no SAP business partner on their rep and no " +
                    "sale naming one, so their invoices were not read and every sale shows as not in SAP.";
            }

            if (SalesForWarehousesWithNoSnapshot > 0)
            {
                yield return
                    $"{SalesForWarehousesWithNoSnapshot:N0} sale line(s) were made from a van with no " +
                    "count, so they sold stock this report never saw arrive.";
            }
        }
    }
}
