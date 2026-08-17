namespace ShopInventory.Web.Models;

// The two van stock reports' DTOs, hand-mirrored from the API as everything in this project is.
// Nullability has to match exactly: a property declared non-nullable here against a value the API
// can send as null makes System.Text.Json throw, and the page reports "no data" rather than an error.
//
// The nulls in these two carry the same weight they do elsewhere in the van reports. A van that
// asked for nothing has no service level rather than a perfect one; a van loaded with nothing has no
// sell-through; a variance across a gap in the snapshots is unavailable, not zero.

// ── Replenishment ───────────────────────────────────────────────────────────────

public class VanReplenishmentReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public VanReplenishmentSummary Summary { get; set; } = new();
    public List<VanReplenishmentVan> Vans { get; set; } = [];
    public List<VanReplenishmentRequest> NeedingAttention { get; set; } = [];
    public VanReplenishmentQuality Quality { get; set; } = new();
}

public class VanReplenishmentSummary
{
    public int VanCount { get; set; }
    public int RequestCount { get; set; }
    public int PostedCount { get; set; }
    public int RejectedCount { get; set; }
    public int AwaitingApprovalCount { get; set; }
    public int PostFailedCount { get; set; }
    public int LineCount { get; set; }
    public double? MedianHoursToDecision { get; set; }
    public double? MedianHoursToPosting { get; set; }

    public double? PostRate => RequestCount > 0 ? (double)PostedCount / RequestCount : null;

    public double? RejectionRate => RequestCount > 0 ? (double)RejectedCount / RequestCount : null;

    public int NeedingAttentionCount => AwaitingApprovalCount + PostFailedCount;
}

public class VanReplenishmentVan
{
    public string VanWarehouseCode { get; set; } = string.Empty;
    public List<string> DepotWarehouses { get; set; } = [];
    public int RequestCount { get; set; }
    public int PostedCount { get; set; }
    public int RejectedCount { get; set; }
    public int AwaitingApprovalCount { get; set; }
    public int PostFailedCount { get; set; }
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public double? MedianHoursToDecision { get; set; }
    public double? MedianHoursToPosting { get; set; }
    public double? SlowestHoursToPosting { get; set; }
    public DateTime? LastRequestedAt { get; set; }
    public DateTime? LastPostedAt { get; set; }

    /// <summary>Null means never supplied at all — a different finding from a long gap.</summary>
    public int? DaysSinceLastPosted { get; set; }

    public double? PostRate => RequestCount > 0 ? (double)PostedCount / RequestCount : null;

    public double? RejectionRate => RequestCount > 0 ? (double)RejectedCount / RequestCount : null;

    public int NeedingAttentionCount => AwaitingApprovalCount + PostFailedCount;
}

public class VanReplenishmentRequest
{
    public Guid Id { get; set; }
    public string VanWarehouseCode { get; set; } = string.Empty;
    public string DepotWarehouseCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public string? LastError { get; set; }
    public double HoursWaiting { get; set; }

    public bool IsPostFailure => string.Equals(Status, "PostFailed", StringComparison.OrdinalIgnoreCase);

    public int DaysWaiting => (int)(HoursWaiting / 24);

    /// <summary>
    /// What a reader needs to do about it. A failed post was decided and then lost; one merely
    /// awaiting approval is waiting on a person.
    /// </summary>
    public string GapLabel => IsPostFailure ? "Failed to post" : "Awaiting a decision";
}

public class VanReplenishmentQuality
{
    public int RequestsWithoutDecisionTime { get; set; }
    public int RequestsWithoutPostTime { get; set; }
    public int PostedWithoutSapDocNum { get; set; }
    public int VansWithNoRequests { get; set; }

    public bool IsClean =>
        RequestsWithoutDecisionTime == 0
        && RequestsWithoutPostTime == 0
        && PostedWithoutSapDocNum == 0
        && VansWithNoRequests == 0;

    public IEnumerable<string> Caveats
    {
        get
        {
            if (VansWithNoRequests > 0)
            {
                yield return
                    $"{VansWithNoRequests:N0} van(s) raised no restock request at all in this period, " +
                    "so they have no service level to measure.";
            }

            if (PostedWithoutSapDocNum > 0)
            {
                yield return
                    $"{PostedWithoutSapDocNum:N0} request(s) are marked posted but carry no SAP " +
                    "document number, so the posting cannot be confirmed against SAP.";
            }

            if (RequestsWithoutDecisionTime > 0)
            {
                yield return
                    $"{RequestsWithoutDecisionTime:N0} decided request(s) recorded no decision time, " +
                    "so they are left out of the waiting figures rather than counted as instant.";
            }

            if (RequestsWithoutPostTime > 0)
            {
                yield return
                    $"{RequestsWithoutPostTime:N0} posted request(s) recorded no posting time and are " +
                    "likewise excluded from the waiting figures.";
            }
        }
    }
}

// ── Stock ───────────────────────────────────────────────────────────────────────

public class VanStockReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int DeadStockDays { get; set; }
    public VanStockSummary Summary { get; set; } = new();
    public List<VanStockDay> Days { get; set; } = [];
    public List<VanStockVariance> Variances { get; set; } = [];
    public List<VanStockItem> Items { get; set; } = [];
    public List<VanStockExpiry> Expiring { get; set; } = [];
    public VanStockQuality Quality { get; set; } = new();
}

public class VanStockSummary
{
    public int VanCount { get; set; }
    public int SnapshotDayCount { get; set; }
    public int MissingSnapshotDays { get; set; }
    public int ItemCount { get; set; }
    public int DeadItemCount { get; set; }
    public decimal LoadedQuantity { get; set; }
    public decimal SoldQuantity { get; set; }
    public DateTime? LatestSnapshotDate { get; set; }
    public int? SnapshotAgeDays { get; set; }

    public double? SellThroughRate =>
        LoadedQuantity > 0 ? (double)(SoldQuantity / LoadedQuantity) : null;

    public bool IsStale => SnapshotAgeDays is > 0;
}

public class VanStockDay
{
    public string VanWarehouseCode { get; set; } = string.Empty;
    public DateTime SnapshotDate { get; set; }
    public bool SnapshotComplete { get; set; }
    public int ItemCount { get; set; }
    public decimal LoadedQuantity { get; set; }
    public decimal SoldQuantity { get; set; }
    public decimal AdjustmentQuantity { get; set; }
    public int SoldItemCount { get; set; }
    public int UnsoldItemCount { get; set; }

    public decimal ExpectedRemaining => LoadedQuantity - SoldQuantity + AdjustmentQuantity;

    public double? SellThroughRate =>
        LoadedQuantity > 0 ? (double)(SoldQuantity / LoadedQuantity) : null;

    public bool SoldBeyondLoad => SoldQuantity > LoadedQuantity + AdjustmentQuantity;
}

public class VanStockVariance
{
    public string VanWarehouseCode { get; set; } = string.Empty;
    public DateTime FromSnapshot { get; set; }
    public DateTime ToSnapshot { get; set; }
    public bool HasGap { get; set; }
    public int GapDays { get; set; }
    public decimal OpeningQuantity { get; set; }
    public decimal SoldQuantity { get; set; }
    public decimal AdjustmentQuantity { get; set; }
    public decimal ClosingQuantity { get; set; }
    public int ItemsShort { get; set; }
    public int ItemsOver { get; set; }
    public List<VanStockItemVariance> TopVariances { get; set; } = [];

    public decimal? ExpectedQuantity =>
        HasGap ? null : OpeningQuantity - SoldQuantity + AdjustmentQuantity;

    public decimal? Variance =>
        ExpectedQuantity is { } expected ? decimal.Round(ClosingQuantity - expected, 3) : null;

    public double? VariancePercent =>
        ExpectedQuantity is { } expected && expected != 0
            ? (double)decimal.Round((ClosingQuantity - expected) / expected * 100m, 2)
            : null;
}

public class VanStockItemVariance
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Expected { get; set; }
    public decimal Actual { get; set; }

    public decimal Variance => decimal.Round(Actual - Expected, 3);

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

public class VanStockItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public int VanCount { get; set; }
    public int DaysOnVan { get; set; }
    public int DaysSold { get; set; }
    public int DaysOnVanWithoutSelling { get; set; }
    public decimal LoadedQuantity { get; set; }
    public decimal SoldQuantity { get; set; }
    public DateTime? LastSoldOn { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;

    public double? SellThroughRate =>
        LoadedQuantity > 0 ? (double)(SoldQuantity / LoadedQuantity) : null;

    public bool IsDead => DaysSold == 0 && DaysOnVan > 0;
}

public class VanStockExpiry
{
    public string VanWarehouseCode { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int DaysToExpiry { get; set; }
    public decimal Quantity { get; set; }
    public DateTime SnapshotDate { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;

    public bool HasExpired => DaysToExpiry < 0;
}

public class VanStockQuality
{
    public int MissingSnapshotDays { get; set; }
    public int IncompleteSnapshots { get; set; }
    public int VansWithNoSnapshot { get; set; }
    public int VariancePairsSkippedForGaps { get; set; }
    public int SalesForWarehousesWithNoSnapshot { get; set; }
    public DateTime? LatestSnapshotDate { get; set; }
    public int? SnapshotAgeDays { get; set; }

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
