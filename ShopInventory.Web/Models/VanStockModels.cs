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

/// <summary>
/// Mirrors the API's van stock report: each morning's SAP count against the SAP documents created
/// since the count before it, and each trading day's sales against the SAP invoices dated that day.
/// </summary>
public class VanStockReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int DeadStockDays { get; set; }
    public VanStockSummary Summary { get; set; } = new();
    public List<VanStockDay> Days { get; set; } = [];
    public List<VanStockMorning> Mornings { get; set; } = [];
    public List<VanStockSalesDay> SalesDays { get; set; } = [];
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

    /// <summary>The business partners this van's sales invoice to in SAP.</summary>
    public List<string> AccountCodes { get; set; } = [];

    public int MorningsChecked { get; set; }
    public int MorningsTied { get; set; }
    public int SalesDays { get; set; }
    public int SalesDaysOnTime { get; set; }
    public int SalesDaysLate { get; set; }
    public int SalesDaysNotInSap { get; set; }
    public int SalesDaysSapOnly { get; set; }
    public int? MaxDaysLate { get; set; }

    public int MorningsWithOtherPostings => MorningsChecked - MorningsTied;

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

    /// <summary>False when SAP could not be read: nothing is then claimed about SAP either way.</summary>
    public bool SapChecked { get; set; }

    /// <summary>When the newest count read SAP, on the CAT clock.</summary>
    public DateTime? LatestCountAt { get; set; }

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

/// <summary>One van between two counts: what SAP posted in between, and whether it accounts for the change.</summary>
public class VanStockMorning
{
    public string VanWarehouseCode { get; set; } = string.Empty;
    public DateTime FromSnapshot { get; set; }
    public DateTime ToSnapshot { get; set; }
    public DateTime CountedFrom { get; set; }
    public DateTime CountedTo { get; set; }
    public int GapDays { get; set; }
    public bool SapChecked { get; set; }
    public int ItemCount { get; set; }
    public int ItemsMoved { get; set; }
    public int ItemsUnexplained { get; set; }
    public List<VanStockDocument> Documents { get; set; } = [];
    public List<VanStockItemMovement> Unexplained { get; set; } = [];

    public bool HasGap => GapDays > 1;

    public bool TiesToSap => SapChecked && ItemsUnexplained == 0;
}

public class VanStockDocument
{
    /// <summary>Invoice, CreditNote, TransferIn or TransferOut.</summary>
    public string Kind { get; set; } = string.Empty;
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public DateTime DocDate { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>False for a transfer SAP stamped with a date only and the listener never saw.</summary>
    public bool CreatedTimeKnown { get; set; }
    public int ItemCount { get; set; }
    public string? Comments { get; set; }

    public int DaysBackdated => Math.Max(0, (int)(CreatedAt.Date - DocDate.Date).TotalDays);
}

public class VanStockItemMovement
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Opening { get; set; }
    public decimal Invoiced { get; set; }
    public decimal Credited { get; set; }
    public decimal TransferredIn { get; set; }
    public decimal TransferredOut { get; set; }
    public decimal Closing { get; set; }

    public decimal Expected => Opening - Invoiced + Credited + TransferredIn - TransferredOut;

    public decimal Unexplained => decimal.Round(Closing - Expected, 3);

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

/// <summary>One van's trading day: what the van recorded against the SAP invoices dated that day.</summary>
public class VanStockSalesDay
{
    public string VanWarehouseCode { get; set; } = string.Empty;
    public DateTime TradingDate { get; set; }

    /// <summary>OnTime, Late, NotInSap, Pending, SapOnly or Unchecked.</summary>
    public string Status { get; set; } = string.Empty;
    public int RecordedItems { get; set; }
    public int SapItems { get; set; }
    public int SapInvoiceCount { get; set; }
    public DateTime? FirstInvoicedAt { get; set; }
    public DateTime? LastInvoicedAt { get; set; }
    public DateTime NextCountAt { get; set; }
    public int? DaysLate { get; set; }
    public int ItemsNotInSap { get; set; }
    public int ItemsOnlyInSap { get; set; }
    public List<VanStockSalesItem> Differences { get; set; } = [];
}

public class VanStockSalesItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Recorded { get; set; }
    public decimal Invoiced { get; set; }

    public decimal Difference => decimal.Round(Invoiced - Recorded, 3);

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

/// <summary>
/// What the period could not answer. The sentences are the API's own, carried as sent, so the page
/// and the report cannot word the same gap two ways.
/// </summary>
public class VanStockQuality
{
    public int MissingSnapshotDays { get; set; }
    public int IncompleteSnapshots { get; set; }
    public int VansWithNoSnapshot { get; set; }
    public int SalesForWarehousesWithNoSnapshot { get; set; }
    public DateTime? LatestSnapshotDate { get; set; }
    public int? SnapshotAgeDays { get; set; }
    public string? SapProblem { get; set; }
    public int VansWithNoAccount { get; set; }
    public bool IsClean { get; set; } = true;
    public List<string> Caveats { get; set; } = [];
}
