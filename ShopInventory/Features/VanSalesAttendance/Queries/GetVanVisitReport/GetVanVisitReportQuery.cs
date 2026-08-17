using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisitReport;

/// <summary>
/// Time on the round, summarised per rep.
///
/// The van counterpart of the merchandiser timesheet report, and not a copy of it. Two differences
/// are deliberate:
///
/// - <b>Days are CAT trading days.</b> The merchandiser report groups on the raw UTC date, which
///   files an 18:30 CAT call under the following day. A van's evening calls are ordinary, and the
///   departure compliance report already counts in CAT trading days — the two van reports have to
///   agree on what a day is or a supervisor reading them side by side sees different call counts.
/// - <b>Open and offline calls are counted.</b> A call never checked out, and a call that reached
///   the server hours after it happened, are both routine on a van and both worth seeing; on the
///   merchandiser side they are rare enough that the report never named them.
/// </summary>
/// <param name="FromDate">Inclusive CAT trading day.</param>
/// <param name="ToDate">Inclusive CAT trading day.</param>
/// <param name="UserId">One rep, or every rep when omitted.</param>
/// <param name="Username">One rep by username.</param>
public sealed record GetVanVisitReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId,
    string? Username
) : IRequest<ErrorOr<VanVisitReportResult>>;

public sealed record VanVisitReportResult(
    DateTime FromDate,
    DateTime ToDate,
    List<VanVisitReportRepSummary> RepSummaries,
    int TotalCalls,
    int CompletedCalls,
    int OpenCalls,
    int OfflineCalls,
    double TotalHours,
    double AverageCallMinutes,
    int TradingDays
);

/// <param name="UserId">The rep these figures are for.</param>
/// <param name="Username">The rep's username.</param>
/// <param name="FullName">
/// The rep's display name, taken from the first call in the period that carries one. Null when no
/// call does.
/// </param>
/// <param name="TotalCalls">Every call checked in over the period, open ones included.</param>
/// <param name="CompletedCalls">Calls that checked out.</param>
/// <param name="OpenCalls">Calls never checked out.</param>
/// <param name="OfflineCalls">
/// Calls the handset captured offline and uploaded later. Counted alongside the rest rather than
/// instead of them: an offline call is an ordinary call that took a slower road to the server.
/// </param>
/// <param name="DistinctCustomers">Customer codes, counted once each — a return visit is one customer, two calls.</param>
/// <param name="TradingDays">CAT trading days on which the rep checked into at least one customer.</param>
/// <param name="TotalMinutes">Summed over the calls that closed; an open call has no duration to add.</param>
/// <param name="AverageMinutesPerCall">
/// <see cref="TotalMinutes"/> over <see cref="CompletedCalls"/>, not over <see cref="TotalCalls"/> —
/// dividing by calls that never closed would drag the average down with time nobody spent. Zero when
/// no call in the period closed.
/// </param>
/// <param name="Days">The rep's days, newest first.</param>
/// <param name="Customers">
/// Who the rep called on, busiest first and then by name. One row per customer code, however many
/// times it was visited.
/// </param>
/// <param name="RouteCode">
/// The route the rep ran on the latest day of the period they worked, taken from that day's
/// snapshot. A rep moved to another route mid-period shows the route they are on now rather than a
/// blend of the two, and the per-day route on <see cref="VanVisitReportDaySummary"/> is where the
/// history stays honest. Null when no day in the period was opened on the handset.
/// </param>
/// <param name="RouteName">The same round's route name.</param>
public sealed record VanVisitReportRepSummary(
    Guid UserId,
    string Username,
    string? FullName,
    int TotalCalls,
    int CompletedCalls,
    int OpenCalls,
    int OfflineCalls,
    int DistinctCustomers,
    int TradingDays,
    double TotalMinutes,
    double AverageMinutesPerCall,
    List<VanVisitReportDaySummary> Days,
    List<VanVisitReportCustomerSummary> Customers,
    string? RouteCode = null,
    string? RouteName = null
);

/// <param name="Date">The CAT trading day. A date, not an instant — it carries no time of day.</param>
/// <param name="CallCount">Every call checked in that day, open ones included.</param>
/// <param name="DistinctCustomers">Customer codes, counted once each — a return visit is one customer, two calls.</param>
/// <param name="OpenCalls">Calls never checked out.</param>
/// <param name="TotalMinutes">Summed over the calls that closed; an open call has no duration to add.</param>
/// <param name="FirstCheckIn">UTC, like every other instant the API sends. The page converts.</param>
/// <param name="LastCheckOut">UTC as well, and null until a call on that day closes.</param>
/// <param name="Calls">
/// The day's calls in check-in order.
///
/// Carried because the attendance page draws the day rather than tabulating it: a strip of the hours
/// between the first check-in and the last check-out, with the calls marked on it, only exists if the
/// individual calls are here. The same rows are what let the page find the gaps between calls — a
/// measure the API deliberately does not compute, because "how long is a long gap" is a rule the
/// reader sets and the answer has to change with it, without a round trip.
///
/// Four fields rather than the whole <c>VanVisitDto</c>: the coordinates, notes and capture sources
/// belong to the call-by-call ledger on /van-sales/activity, and sending them here would multiply the
/// payload for a page that draws none of them.
/// </param>
/// <param name="RouteCode">
/// The route this round ran on, snapshotted onto the rep's day when they started it on the handset.
/// Null when the rep checked into customers without starting a day — routine, and itself a finding.
/// </param>
/// <param name="RouteName">The same round's route name.</param>
public sealed record VanVisitReportDaySummary(
    DateTime Date,
    int CallCount,
    int DistinctCustomers,
    int OpenCalls,
    double TotalMinutes,
    DateTime? FirstCheckIn,
    DateTime? LastCheckOut,
    List<VanVisitReportCallSummary> Calls,
    string? RouteCode = null,
    string? RouteName = null
);

/// <summary>
/// One call on the strip: who it was at, when the rep arrived, and when they left.
/// </summary>
/// <param name="CustomerCode">The customer called on.</param>
/// <param name="CustomerName">The customer's name as the call recorded it, so the strip can label the block.</param>
/// <param name="CheckInTime">UTC. The page converts to CAT, as it does with every other instant.</param>
/// <param name="CheckOutTime">
/// UTC, and null for a call never checked out. Null rather than a copy of the check-in: the page
/// draws an open call as a stub with a ring round it and names it, and a zero-length block would
/// vanish into the strip instead.
/// </param>
public sealed record VanVisitReportCallSummary(
    string CustomerCode,
    string CustomerName,
    DateTime CheckInTime,
    DateTime? CheckOutTime
);

public sealed record VanVisitReportCustomerSummary(
    string CustomerCode,
    string CustomerName,
    int CallCount,
    double TotalMinutes
);
