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
    public DateTime GeneratedAt { get; set; }
    public VanReplenishmentSummary Summary { get; set; } = new();
    public List<VanReplenishmentWaitBand> Waits { get; set; } = [];
    public List<VanReplenishmentVan> Vans { get; set; } = [];

    /// <summary>Every request still open today, whatever the period — not only the period's.</summary>
    public List<VanReplenishmentOpenRequest> Unfilled { get; set; } = [];

    public List<VanReplenishmentDepotShortage> DepotShortages { get; set; } = [];
    public VanReplenishmentQuality Quality { get; set; } = new();

    /// <summary>The van filter's choices, taken before any filter applied.</summary>
    public List<string> AvailableVans { get; set; } = [];

    /// <summary>The depot filter's choices, taken before any filter applied.</summary>
    public List<string> AvailableDepots { get; set; } = [];
}

public class VanReplenishmentSummary
{
    public int VanCount { get; set; }
    public int VansAsking { get; set; }
    public int RequestCount { get; set; }
    public int PostedCount { get; set; }
    public int PartlyPostedCount { get; set; }
    public int RejectedCount { get; set; }
    public int CancelledCount { get; set; }
    public int WithdrawnAfterFailureCount { get; set; }
    public int OpenCount { get; set; }
    public int LineCount { get; set; }
    public int FilledWithinDayCount { get; set; }
    public double? MedianHoursToDecision { get; set; }
    public double? SlowestTenthHoursToDecision { get; set; }
    public double? MedianHoursToPosting { get; set; }
    public double? SlowestTenthHoursToPosting { get; set; }
    public int UnfilledNowCount { get; set; }
    public int VansWaitingNow { get; set; }
    public int? OldestUnfilledDays { get; set; }

    /// <summary>
    /// The requests a van was owed an answer on: all but those turned down and those its requester
    /// took back before a decision. A withdrawal after a failed post stays in — that van went without.
    /// </summary>
    public int FillBase => RequestCount - RejectedCount - CancelledCount;

    /// <summary>Null on a period with nothing owed — no service level, not a perfect one.</summary>
    public double? FilledWithinDayRate => FillBase > 0 ? (double)FilledWithinDayCount / FillBase : null;

    public double? RejectionRate => RequestCount > 0 ? (double)RejectedCount / RequestCount : null;
}

public class VanReplenishmentWaitBand
{
    public string Band { get; set; } = string.Empty;
    public int Count { get; set; }
}

public static class VanReplenishmentWaitBands
{
    public const string UnderOneHour = "UnderOneHour";
    public const string OneToFourHours = "OneToFourHours";
    public const string FourToTwentyFourHours = "FourToTwentyFourHours";
    public const string OneToThreeDays = "OneToThreeDays";
    public const string OverThreeDays = "OverThreeDays";
    public const string PostedUntimed = "PostedUntimed";
    public const string StillOpen = "StillOpen";
    public const string WithdrawnAfterFailure = "WithdrawnAfterFailure";
    public const string TurnedDown = "TurnedDown";
    public const string CancelledByRequester = "CancelledByRequester";

    public static string Label(string band) => band switch
    {
        UnderOneHour => "Under 1 hour",
        OneToFourHours => "1–4 hours",
        FourToTwentyFourHours => "4–24 hours",
        OneToThreeDays => "1–3 days",
        OverThreeDays => "Over 3 days",
        PostedUntimed => "Posted, no time kept",
        StillOpen => "Still unfilled",
        WithdrawnAfterFailure => "Withdrawn unfilled",
        TurnedDown => "Turned down",
        CancelledByRequester => "Taken back by requester",
        _ => band
    };
}

public class VanReplenishmentVan
{
    public string VanWarehouseCode { get; set; } = string.Empty;
    public List<string> DepotWarehouses { get; set; } = [];

    /// <summary>False when no active rep is assigned to the van any more.</summary>
    public bool IsAssigned { get; set; }

    public int RequestCount { get; set; }
    public int PostedCount { get; set; }
    public int PartlyPostedCount { get; set; }
    public int RejectedCount { get; set; }
    public int CancelledCount { get; set; }
    public int WithdrawnAfterFailureCount { get; set; }
    public int OpenCount { get; set; }
    public int FilledWithinDayCount { get; set; }
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public double? MedianHoursToDecision { get; set; }
    public double? MedianHoursToPosting { get; set; }
    public double? SlowestHoursToPosting { get; set; }
    public DateTime? LastRequestedAt { get; set; }

    /// <summary>The van's last load over all time — not only this period.</summary>
    public DateTime? LastPostedAt { get; set; }

    /// <summary>Null means never supplied at all — a different finding from a long gap.</summary>
    public int? DaysSinceLastPosted { get; set; }

    public int UnfilledNowCount { get; set; }
    public bool LastPostedBeforePeriod { get; set; }

    public int FillBase => RequestCount - RejectedCount - CancelledCount;

    public double? FilledWithinDayRate => FillBase > 0 ? (double)FilledWithinDayCount / FillBase : null;

    /// <summary>What never reached the van: still open, or withdrawn after failing to post.</summary>
    public int UnfilledCount => OpenCount + WithdrawnAfterFailureCount;
}

public class VanReplenishmentOpenRequest
{
    public Guid Id { get; set; }
    public string? DraftNumber { get; set; }
    public string VanWarehouseCode { get; set; } = string.Empty;
    public string DepotWarehouseCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Cause { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string? RequestedByRole { get; set; }

    /// <summary>Raised by depot staff on the van's behalf rather than by the van's rep.</summary>
    public bool RaisedByDepot { get; set; }

    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? LastAttemptedAt { get; set; }
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public int? ShortLineCount { get; set; }
    public bool ShortLinesIncomplete { get; set; }
    public List<VanReplenishmentShortItem> ShortItems { get; set; } = [];
    public string? LastError { get; set; }
    public double HoursWaiting { get; set; }
    public int DaysWaiting { get; set; }
    public int? LinesInStockAtLastAttempt { get; set; }
}

/// <summary>Why an open request is open. Mirrors the API's causes.</summary>
public static class VanReplenishmentCauses
{
    public const string AwaitingDecision = "AwaitingDecision";
    public const string Posting = "Posting";
    public const string ApprovedNeverPosted = "ApprovedNeverPosted";
    public const string DepotShort = "DepotShort";
    public const string StockUnread = "StockUnread";
    public const string OutcomeUnknown = "OutcomeUnknown";
    public const string PostRefused = "PostRefused";

    /// <summary>The cause in a few words, as the page's chips and the export's column say it.</summary>
    public static string Label(string cause) => cause switch
    {
        AwaitingDecision => "Waiting on an approver",
        Posting => "Posting now",
        ApprovedNeverPosted => "Approved, never posted",
        DepotShort => "Depot short",
        StockUnread => "SAP stock unread",
        OutcomeUnknown => "Outcome unknown",
        PostRefused => "SAP refused",
        _ => cause
    };
}

public class VanReplenishmentShortItem
{
    public string ItemCode { get; set; } = string.Empty;
    public decimal Shortage { get; set; }
}

public class VanReplenishmentDepotShortage
{
    public string DepotWarehouseCode { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int VanCount { get; set; }
    public int LineCount { get; set; }
    public int LinesInStock { get; set; }
    public bool Incomplete { get; set; }
    public List<VanReplenishmentDepotShortItem> Items { get; set; } = [];
}

public class VanReplenishmentDepotShortItem
{
    public string ItemCode { get; set; } = string.Empty;
    public decimal Shortage { get; set; }
    public int RequestCount { get; set; }
}

public class VanReplenishmentQuality
{
    public int RequestsWithoutDecisionTime { get; set; }
    public int RequestsWithoutPostTime { get; set; }
    public int PostedWithoutSapDocNum { get; set; }
    public int VansWithNoRequests { get; set; }
    public int PostsRecordedByHand { get; set; }

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
                    "so they have no wait to measure.";
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

// ── A held transfer against the depot, live ─────────────────────────────────────

public class PendingTransferStockCheck
{
    public Guid PendingTransferId { get; set; }
    public string FromWarehouse { get; set; } = string.Empty;
    public string ToWarehouse { get; set; } = string.Empty;
    public DateTime CheckedAt { get; set; }
    public bool StockWasFullyRead { get; set; }
    public List<string> UnreadableWarehouses { get; set; } = [];
    public List<PendingTransferStockCheckLine> Lines { get; set; } = [];
    public int LinesInStock { get; set; }
    public int LinesShort { get; set; }
}

public class PendingTransferStockCheckLine
{
    public int LineNumber { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? UoMCode { get; set; }
    public string? BatchNumber { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>Known only for a short line.</summary>
    public decimal? AvailableQuantity { get; set; }

    /// <summary>InStock, Short or Unread.</summary>
    public string State { get; set; } = string.Empty;

    public bool IsShort => State == "Short";
    public bool IsUnread => State == "Unread";
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
    public List<VanStockVan> Vans { get; set; } = [];
}

/// <summary>
/// One van across the period, in counts of items: a van carries cases, kilograms and singles, and
/// a sum across them is a figure in no unit.
/// </summary>
public class VanStockVan
{
    public string VanWarehouseCode { get; set; } = string.Empty;
    public int DaysCounted { get; set; }
    public int DaysWithSales { get; set; }
    public int ItemCount { get; set; }
    public int ItemDays { get; set; }
    public int SoldItemDays { get; set; }
    public int DeadItemCount { get; set; }

    /// <summary>Of the item-mornings carried, the share on which the item sold. Null when nothing was carried.</summary>
    public double? ItemsSellingRate => ItemDays > 0 ? (double)SoldItemDays / ItemDays : null;
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

    /// <summary>Items on the van either morning. Zero from an API that predates it.</summary>
    public int ItemCount { get; set; }

    public int ItemsMatched => Math.Max(0, ItemCount - ItemsShort - ItemsOver);

    public bool Balanced => !HasGap && ItemsShort == 0 && ItemsOver == 0;

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
    public decimal Opening { get; set; }
    public decimal Sold { get; set; }
    public decimal Adjustment { get; set; }

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
