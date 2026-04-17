using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Timesheets.Queries.GetTimesheetReport;

public sealed record GetTimesheetReportQuery(
    Guid? UserId,
    string? Username,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<TimesheetReportResult>>;

public sealed record TimesheetReportResult(
    DateTime FromDate,
    DateTime ToDate,
    List<TimesheetReportUserSummary> UserSummaries,
    int TotalVisits,
    double TotalHours,
    double AverageVisitMinutes
);

public sealed record TimesheetReportUserSummary(
    Guid UserId,
    string Username,
    int TotalVisits,
    int CompletedVisits,
    double TotalMinutes,
    double AverageMinutesPerVisit,
    List<TimesheetReportDailySummary> DailySummaries,
    List<TimesheetReportCustomerSummary> CustomerSummaries
);

public sealed record TimesheetReportDailySummary(
    DateTime Date,
    int VisitCount,
    double TotalMinutes,
    DateTime? FirstCheckIn,
    DateTime? LastCheckOut
);

public sealed record TimesheetReportCustomerSummary(
    string CustomerCode,
    string CustomerName,
    int VisitCount,
    double TotalMinutes
);
