using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanReplenishmentReport;

/// <summary>
/// How well the depots are keeping the vans stocked.
/// </summary>
/// <remarks>
/// Reads <c>PendingInventoryTransfers</c>, which is where a van's restock request lands before it
/// becomes a SAP document. That table records the three moments the service level actually turns on —
/// when the rep asked, when somebody decided, and when it reached SAP — so the waiting can be
/// measured rather than guessed at.
///
/// Deliberately independent of the daily stock snapshot. Snapshots are a desktop-app feature that van
/// sales never write to, and the job that fills them is off by default, so a report that needed them
/// would silently report nothing. This one answers what was issued to each van and how long it took,
/// from data the van flow itself creates.
/// </remarks>
public sealed record GetVanReplenishmentReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    string? VanWarehouseCode = null
) : IRequest<ErrorOr<VanReplenishmentReportResult>>;

public sealed record VanReplenishmentReportResult(
    DateTime FromDate,
    DateTime ToDate,
    VanReplenishmentSummaryResult Summary,
    List<VanReplenishmentVanResult> Vans,
    List<VanReplenishmentRequestResult> NeedingAttention,
    VanReplenishmentQualityResult Quality
);

// ── Summary ─────────────────────────────────────────────────────────────────────

public sealed record VanReplenishmentSummaryResult(
    int VanCount,
    int RequestCount,
    int PostedCount,
    int RejectedCount,
    int AwaitingApprovalCount,
    int PostFailedCount,
    int LineCount,
    double? MedianHoursToDecision,
    double? MedianHoursToPosting)
{
    /// <summary>
    /// The share of requests that reached SAP. Null on a period with none — an empty period has no
    /// service level, and 100% would be the flattering reading of nothing happening.
    /// </summary>
    public double? PostRate =>
        RequestCount > 0 ? (double)PostedCount / RequestCount : null;

    public double? RejectionRate =>
        RequestCount > 0 ? (double)RejectedCount / RequestCount : null;

    /// <summary>Requests that need somebody: still waiting for a decision, or failed to post.</summary>
    public int NeedingAttentionCount => AwaitingApprovalCount + PostFailedCount;
}

// ── Per van ─────────────────────────────────────────────────────────────────────

/// <summary>
/// One van's restocking over the period.
/// </summary>
/// <remarks>
/// <c>LineCount</c> is the size measure to trust. <c>TotalQuantity</c> is carried because the table
/// stores it, but it is the sum of every line's quantity regardless of unit — crates added to kilos
/// added to eaches — so it indicates the rough scale of a load and nothing finer. Nothing divides by
/// it.
///
/// The two waits are separated on purpose. Time to a decision is somebody's attention; time from
/// decision to SAP is the posting machinery. A van kept waiting by the first needs a different
/// conversation from one kept waiting by the second.
/// </remarks>
public sealed record VanReplenishmentVanResult(
    string VanWarehouseCode,
    List<string> DepotWarehouses,
    int RequestCount,
    int PostedCount,
    int RejectedCount,
    int AwaitingApprovalCount,
    int PostFailedCount,
    int LineCount,
    decimal TotalQuantity,
    double? MedianHoursToDecision,
    double? MedianHoursToPosting,
    double? SlowestHoursToPosting,
    DateTime? LastRequestedAt,
    DateTime? LastPostedAt)
{
    public double? PostRate => RequestCount > 0 ? (double)PostedCount / RequestCount : null;

    public double? RejectionRate => RequestCount > 0 ? (double)RejectedCount / RequestCount : null;

    public int NeedingAttentionCount => AwaitingApprovalCount + PostFailedCount;

    /// <summary>
    /// Days since this van last had a load posted. Null when none ever was, which is a different
    /// finding from a long gap — one van is badly served, the other has never been served at all.
    /// </summary>
    public int? DaysSinceLastPosted { get; init; }
}

// ── The worklist ────────────────────────────────────────────────────────────────

/// <summary>
/// A request that has not reached SAP and is not going to without somebody.
/// </summary>
/// <remarks>
/// Two quite different problems share this list and the status tells them apart. One is waiting for a
/// human decision. The other was decided and then failed to post — which is the case worth watching,
/// because a disconnect during posting can leave a document stranded with no error surfaced anywhere
/// else.
/// </remarks>
public sealed record VanReplenishmentRequestResult(
    Guid Id,
    string VanWarehouseCode,
    string DepotWarehouseCode,
    string Status,
    string RequestedBy,
    DateTime RequestedAt,
    DateTime? DecidedAt,
    int LineCount,
    decimal TotalQuantity,
    string? LastError,
    double HoursWaiting)
{
    public bool IsPostFailure => string.Equals(Status, "PostFailed", StringComparison.OrdinalIgnoreCase);

    public int DaysWaiting => (int)(HoursWaiting / 24);
}

// ── Data quality ────────────────────────────────────────────────────────────────

public sealed record VanReplenishmentQualityResult(
    int RequestsWithoutDecisionTime,
    int RequestsWithoutPostTime,
    int PostedWithoutSapDocNum,
    int VansWithNoRequests)
{
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
