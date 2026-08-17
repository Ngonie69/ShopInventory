using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;

/// <summary>
/// What went onto each van, what sold off it, and what the next morning found.
/// </summary>
/// <remarks>
/// Built on the morning stock snapshot, with two things about that table settled up front because
/// both are easy to assume wrongly.
///
/// <b>The snapshot is not a running total for a van.</b> <c>AvailableQuantity</c> is decremented by
/// the desktop till, and no van sales path touches it — so on a van warehouse it is not "what is left
/// on board". Only <c>OriginalQuantity</c> means anything here: it is taken fresh from SAP each
/// morning, so it is the load. What sold is computed from the van sales themselves.
///
/// <b>The reconciliation is therefore morning to morning.</b> Yesterday's load, less what sold off
/// it, plus any transfer that reached it during the day, is what this morning should have found.
/// The difference between that and what this morning actually found is the variance — and it is only
/// computable across two <em>consecutive</em> snapshots. A missing day breaks the chain and is
/// reported as a break rather than bridged, because bridging it would quietly attribute two days of
/// difference to one.
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
    List<VanStockVarianceResult> Variances,
    List<VanStockItemResult> Items,
    List<VanStockExpiryResult> Expiring,
    VanStockQualityResult Quality
);

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
    int? SnapshotAgeDays)
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

// ── C2: variance ────────────────────────────────────────────────────────────────

/// <summary>
/// What one morning expected to find on a van against what it actually found.
/// </summary>
/// <remarks>
/// Only produced for two consecutive snapshot days. Where a day is missing the pair is emitted with
/// <c>HasGap</c> set and no variance at all, rather than reaching over the gap — two days of
/// difference reported as one would look like a single large discrepancy on the wrong date.
/// </remarks>
public sealed record VanStockVarianceResult(
    string VanWarehouseCode,
    DateTime FromSnapshot,
    DateTime ToSnapshot,
    bool HasGap,
    int GapDays,
    decimal OpeningQuantity,
    decimal SoldQuantity,
    decimal AdjustmentQuantity,
    decimal ClosingQuantity,
    int ItemsShort,
    int ItemsOver,
    List<VanStockItemVarianceResult> TopVariances)
{
    /// <summary>What the second morning should have found, from the first morning's arithmetic.</summary>
    public decimal? ExpectedQuantity =>
        HasGap ? null : OpeningQuantity - SoldQuantity + AdjustmentQuantity;

    /// <summary>
    /// Actual less expected. Negative means stock is missing; positive means more was found than the
    /// day accounts for. Null across a gap.
    /// </summary>
    public decimal? Variance =>
        ExpectedQuantity is { } expected ? decimal.Round(ClosingQuantity - expected, 3) : null;

    public double? VariancePercent =>
        ExpectedQuantity is { } expected && expected != 0
            ? (double)decimal.Round((ClosingQuantity - expected) / expected * 100m, 2)
            : null;
}

public sealed record VanStockItemVarianceResult(
    string ItemCode,
    string? ItemDescription,
    decimal Expected,
    decimal Actual)
{
    public decimal Variance => decimal.Round(Actual - Expected, 3);

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
    int VariancePairsSkippedForGaps,
    int SalesForWarehousesWithNoSnapshot,
    DateTime? LatestSnapshotDate,
    int? SnapshotAgeDays)
{
    public bool IsClean =>
        MissingSnapshotDays == 0
        && IncompleteSnapshots == 0
        && VansWithNoSnapshot == 0
        && VariancePairsSkippedForGaps == 0
        && SalesForWarehousesWithNoSnapshot == 0
        && SnapshotAgeDays is null or 0;

    public IEnumerable<string> Caveats
    {
        get
        {
            if (SnapshotAgeDays is > 0)
            {
                yield return
                    $"The newest stock snapshot is {SnapshotAgeDays:N0} day(s) old" +
                    (LatestSnapshotDate is { } latest ? $" ({latest:dd MMM yyyy})" : "")
                    + ". Every figure here describes the vans as they stood then, not today.";
            }

            if (VansWithNoSnapshot > 0)
            {
                yield return
                    $"{VansWithNoSnapshot:N0} van(s) have no snapshot in this period at all, so nothing " +
                    "can be said about what they carried.";
            }

            if (MissingSnapshotDays > 0)
            {
                yield return
                    $"{MissingSnapshotDays:N0} van-day(s) have no snapshot. Those days are absent from " +
                    "the load figures rather than being filled in from a neighbouring day.";
            }

            if (VariancePairsSkippedForGaps > 0)
            {
                yield return
                    $"{VariancePairsSkippedForGaps:N0} variance(s) could not be computed because the two " +
                    "mornings are not consecutive. Reaching over a gap would report two days of " +
                    "difference as one.";
            }

            if (IncompleteSnapshots > 0)
            {
                yield return
                    $"{IncompleteSnapshots:N0} snapshot(s) did not finish, so their item list may be " +
                    "short and their load understated.";
            }

            if (SalesForWarehousesWithNoSnapshot > 0)
            {
                yield return
                    $"{SalesForWarehousesWithNoSnapshot:N0} sale(s) were made from a warehouse with no " +
                    "snapshot, so they sold stock this report never saw arrive.";
            }
        }
    }
}
