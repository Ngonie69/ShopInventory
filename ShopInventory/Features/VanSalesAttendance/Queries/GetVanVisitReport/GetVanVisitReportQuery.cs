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
    List<VanVisitReportCustomerSummary> Customers
);

/// <param name="Date">The CAT trading day. A date, not an instant — it carries no time of day.</param>
/// <param name="CallCount">Every call checked in that day, open ones included.</param>
/// <param name="DistinctCustomers">Customer codes, counted once each — a return visit is one customer, two calls.</param>
/// <param name="OpenCalls">Calls never checked out.</param>
/// <param name="TotalMinutes">Summed over the calls that closed; an open call has no duration to add.</param>
/// <param name="FirstCheckIn">UTC, like every other instant the API sends. The page converts.</param>
/// <param name="LastCheckOut">UTC as well, and null until a call on that day closes.</param>
public sealed record VanVisitReportDaySummary(
    DateTime Date,
    int CallCount,
    int DistinctCustomers,
    int OpenCalls,
    double TotalMinutes,
    DateTime? FirstCheckIn,
    DateTime? LastCheckOut
);

public sealed record VanVisitReportCustomerSummary(
    string CustomerCode,
    string CustomerName,
    int CallCount,
    double TotalMinutes
);
