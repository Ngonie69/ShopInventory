using ErrorOr;
using MediatR;
using ShopInventory.Data;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Features.Timesheets.Queries.GetTimesheetReport;

public sealed class GetTimesheetReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetTimesheetReportQuery, ErrorOr<TimesheetReportResult>>
{
    public async Task<ErrorOr<TimesheetReportResult>> Handle(
        GetTimesheetReportQuery request,
        CancellationToken cancellationToken)
    {
        var query = db.TimesheetEntries
            .AsNoTracking()
            .Where(t => t.CheckInTime >= request.FromDate && t.CheckInTime <= request.ToDate);

        if (request.UserId.HasValue)
            query = query.Where(t => t.UserId == request.UserId.Value);

        if (!string.IsNullOrWhiteSpace(request.Username))
            query = query.Where(t => t.Username == request.Username);

        var entries = await query
            .OrderBy(t => t.CheckInTime)
            .ToListAsync(cancellationToken);

        var userGroups = entries.GroupBy(e => new { e.UserId, e.Username });

        var userSummaries = new List<TimesheetReportUserSummary>();

        foreach (var group in userGroups)
        {
            var completedVisits = group.Where(e => e.CheckOutTime.HasValue).ToList();

            var dailySummaries = group
                .GroupBy(e => e.CheckInTime.Date)
                .Select(d => new TimesheetReportDailySummary(
                    d.Key,
                    d.Count(),
                    d.Where(e => e.DurationMinutes.HasValue).Sum(e => e.DurationMinutes!.Value),
                    d.Min(e => e.CheckInTime),
                    d.Where(e => e.CheckOutTime.HasValue).Max(e => e.CheckOutTime)))
                .OrderBy(d => d.Date)
                .ToList();

            var customerSummaries = group
                .GroupBy(e => new { e.CustomerCode, e.CustomerName })
                .Select(c => new TimesheetReportCustomerSummary(
                    c.Key.CustomerCode,
                    c.Key.CustomerName,
                    c.Count(),
                    c.Where(e => e.DurationMinutes.HasValue).Sum(e => e.DurationMinutes!.Value)))
                .OrderByDescending(c => c.VisitCount)
                .ToList();

            var totalMinutes = completedVisits
                .Where(e => e.DurationMinutes.HasValue)
                .Sum(e => e.DurationMinutes!.Value);

            userSummaries.Add(new TimesheetReportUserSummary(
                group.Key.UserId,
                group.Key.Username,
                group.Count(),
                completedVisits.Count,
                totalMinutes,
                completedVisits.Count > 0 ? totalMinutes / completedVisits.Count : 0,
                dailySummaries,
                customerSummaries));
        }

        var allTotalMinutes = entries
            .Where(e => e.DurationMinutes.HasValue)
            .Sum(e => e.DurationMinutes!.Value);

        var allCompletedCount = entries.Count(e => e.CheckOutTime.HasValue);

        return new TimesheetReportResult(
            request.FromDate,
            request.ToDate,
            userSummaries,
            entries.Count,
            allTotalMinutes / 60.0,
            allCompletedCount > 0 ? allTotalMinutes / allCompletedCount : 0);
    }
}
