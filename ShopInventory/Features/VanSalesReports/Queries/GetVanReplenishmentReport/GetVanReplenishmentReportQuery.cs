using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanReplenishmentReport;

/// <summary>
/// Whether each van got what it asked the depot for, how long it waited, and what it is still
/// going without.
/// </summary>
/// <remarks>
/// Reads <c>PendingInventoryTransfers</c>, which is where a van's restock request lands before it
/// becomes a SAP document. That table records the three moments the service level actually turns on —
/// when the request was raised, when somebody decided, and when it reached SAP — so the waiting can be
/// measured rather than guessed at.
///
/// Two scopes, deliberately. The figures describe the requests <em>raised</em> in the period. The
/// unfilled list is every request still open today whatever the period, because a request stuck since
/// before the start date is exactly the one a period filter would hide.
///
/// Deliberately independent of the daily stock snapshot. Snapshots are a desktop-app feature that van
/// sales never write to, and the job that fills them is off by default, so a report that needed them
/// would silently report nothing.
/// </remarks>
public sealed record GetVanReplenishmentReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    string? VanWarehouseCode = null,
    string? DepotWarehouseCode = null
) : IRequest<ErrorOr<VanReplenishmentReportResult>>;

public sealed record VanReplenishmentReportResult(
    DateTime FromDate,
    DateTime ToDate,
    DateTime GeneratedAt,
    VanReplenishmentSummaryResult Summary,
    List<VanReplenishmentWaitBandResult> Waits,
    List<VanReplenishmentVanResult> Vans,
    List<VanReplenishmentOpenRequestResult> Unfilled,
    List<VanReplenishmentDepotShortageResult> DepotShortages,
    VanReplenishmentQualityResult Quality,
    List<string> AvailableVans,
    List<string> AvailableDepots
);

// ── Summary ─────────────────────────────────────────────────────────────────────

/// <summary>The period's requests, and what became of them.</summary>
/// <remarks>
/// A request ends one of five ways, and they are kept apart because they mean different things for the
/// van: posted (whole or in part) is served; turned down is a decision somebody made; withdrawn by its
/// requester before a decision never needed serving; withdrawn after it failed to post is a van that
/// went without; and still open is a van still waiting.
/// </remarks>
public sealed record VanReplenishmentSummaryResult(
    int VanCount,
    int VansAsking,
    int RequestCount,
    int PostedCount,
    int PartlyPostedCount,
    int RejectedCount,
    int CancelledCount,
    int WithdrawnAfterFailureCount,
    int OpenCount,
    int LineCount,
    int FilledWithinDayCount,
    double? MedianHoursToDecision,
    double? SlowestTenthHoursToDecision,
    double? MedianHoursToPosting,
    double? SlowestTenthHoursToPosting,
    int UnfilledNowCount,
    int VansWaitingNow,
    int? OldestUnfilledDays)
{
    /// <summary>
    /// The requests a van was owed an answer on: everything but those turned down and those its
    /// requester took back before anybody decided. A withdrawal after a failed post stays in — that
    /// van asked, was approved, and got nothing.
    /// </summary>
    public int FillBase => RequestCount - RejectedCount - CancelledCount;

    /// <summary>
    /// The service level: of the requests a van was owed, the share that reached SAP within a day of
    /// asking. Null on a period with none — an empty period has no service level, and 100% would be
    /// the flattering reading of nothing happening.
    /// </summary>
    public double? FilledWithinDayRate => FillBase > 0 ? (double)FilledWithinDayCount / FillBase : null;

    public double? RejectionRate => RequestCount > 0 ? (double)RejectedCount / RequestCount : null;
}

/// <summary>
/// One band of the wait from asking to SAP, over every request raised in the period — the ones that
/// never got there included, so the chart cannot flatter the depot by leaving them out.
/// </summary>
public sealed record VanReplenishmentWaitBandResult(string Band, int Count);

public static class VanReplenishmentWaitBands
{
    public const string UnderOneHour = "UnderOneHour";
    public const string OneToFourHours = "OneToFourHours";
    public const string FourToTwentyFourHours = "FourToTwentyFourHours";
    public const string OneToThreeDays = "OneToThreeDays";
    public const string OverThreeDays = "OverThreeDays";

    /// <summary>Posted, but with no posting time to measure from.</summary>
    public const string PostedUntimed = "PostedUntimed";

    public const string StillOpen = "StillOpen";
    public const string WithdrawnAfterFailure = "WithdrawnAfterFailure";
    public const string TurnedDown = "TurnedDown";
    public const string CancelledByRequester = "CancelledByRequester";

    public static readonly IReadOnlyList<string> All =
    [
        UnderOneHour, OneToFourHours, FourToTwentyFourHours, OneToThreeDays, OverThreeDays,
        PostedUntimed, StillOpen, WithdrawnAfterFailure, TurnedDown, CancelledByRequester
    ];
}

// ── Per van ─────────────────────────────────────────────────────────────────────

/// <summary>
/// One van's restocking over the period.
/// </summary>
/// <remarks>
/// <c>LineCount</c> is the size measure to trust. <c>TotalQuantity</c> is the sum of every line's
/// quantity regardless of unit — crates added to kilos added to eaches — so it indicates the rough
/// scale of a load and nothing finer. Nothing divides by it.
///
/// <c>LastPostedAt</c> is read over all time, not the period: a van last loaded the day before the
/// period started has been served, and reading only the period would call it "never".
/// </remarks>
public sealed record VanReplenishmentVanResult(
    string VanWarehouseCode,
    List<string> DepotWarehouses,
    bool IsAssigned,
    int RequestCount,
    int PostedCount,
    int PartlyPostedCount,
    int RejectedCount,
    int CancelledCount,
    int WithdrawnAfterFailureCount,
    int OpenCount,
    int FilledWithinDayCount,
    int LineCount,
    decimal TotalQuantity,
    double? MedianHoursToDecision,
    double? MedianHoursToPosting,
    double? SlowestHoursToPosting,
    DateTime? LastRequestedAt,
    DateTime? LastPostedAt,
    int? DaysSinceLastPosted,
    int UnfilledNowCount)
{
    public int FillBase => RequestCount - RejectedCount - CancelledCount;

    public double? FilledWithinDayRate => FillBase > 0 ? (double)FilledWithinDayCount / FillBase : null;

    /// <summary>
    /// True when the van's last load came before the period — served, just not in these dates.
    /// </summary>
    public bool LastPostedBeforePeriod { get; init; }
}

// ── The worklist ────────────────────────────────────────────────────────────────

/// <summary>
/// A request that has not reached SAP and is not going to without somebody.
/// </summary>
public sealed record VanReplenishmentOpenRequestResult(
    Guid Id,
    string? DraftNumber,
    string VanWarehouseCode,
    string DepotWarehouseCode,
    string Status,
    string Cause,
    string RequestedBy,
    string? RequestedByRole,
    bool RaisedByDepot,
    DateTime RequestedAt,
    DateTime? DecidedAt,
    DateTime? LastAttemptedAt,
    int LineCount,
    decimal TotalQuantity,
    int? ShortLineCount,
    bool ShortLinesIncomplete,
    List<VanReplenishmentShortItemResult> ShortItems,
    string? LastError,
    double HoursWaiting)
{
    public int DaysWaiting => (int)(HoursWaiting / 24);

    /// <summary>Lines the depot could fill at the last attempt; null unless the cause is a shortage.</summary>
    public int? LinesInStockAtLastAttempt => ShortLineCount is { } shortLines ? Math.Max(0, LineCount - shortLines) : null;
}

/// <summary>Why an open request is open. Each one needs a different response.</summary>
public static class VanReplenishmentCauses
{
    public const string AwaitingDecision = "AwaitingDecision";

    /// <summary>Approved moments ago; the post is most likely running now.</summary>
    public const string Posting = "Posting";

    /// <summary>
    /// Approved, and no post has run since. The decision saved and the post never started — a crash or
    /// recycle in between — so nothing but this list would ever show it.
    /// </summary>
    public const string ApprovedNeverPosted = "ApprovedNeverPosted";

    public const string DepotShort = "DepotShort";
    public const string StockUnread = "StockUnread";
    public const string OutcomeUnknown = "OutcomeUnknown";
    public const string PostRefused = "PostRefused";
}

public sealed record VanReplenishmentShortItemResult(string ItemCode, decimal Shortage);

/// <summary>
/// What one depot is short of, across every request it cannot fill — the list that tells the depot
/// what to restock, rather than one error per van.
/// </summary>
public sealed record VanReplenishmentDepotShortageResult(
    string DepotWarehouseCode,
    int RequestCount,
    int VanCount,
    int LineCount,
    int LinesInStock,
    bool Incomplete,
    List<VanReplenishmentDepotShortItemResult> Items);

public sealed record VanReplenishmentDepotShortItemResult(string ItemCode, decimal Shortage, int RequestCount);

// ── Data quality ────────────────────────────────────────────────────────────────

public sealed record VanReplenishmentQualityResult(
    int RequestsWithoutDecisionTime,
    int RequestsWithoutPostTime,
    int PostedWithoutSapDocNum,
    int VansWithNoRequests,
    int PostsRecordedByHand)
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
