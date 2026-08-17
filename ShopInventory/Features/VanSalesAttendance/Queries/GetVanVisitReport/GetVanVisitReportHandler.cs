using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisitReport;

public sealed class GetVanVisitReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanVisitReportQuery, ErrorOr<VanVisitReportResult>>
{
    public async Task<ErrorOr<VanVisitReportResult>> Handle(
        GetVanVisitReportQuery request,
        CancellationToken cancellationToken)
    {
        // The caller names CAT trading days; the column stores UTC. Convert once, here, and take the
        // whole of the closing day — FromCAT of the day after, less a tick, rather than the same day
        // at midnight, which would drop every call made after 00:00 CAT on the last date asked for.
        var startUtc = AuditService.FromCAT(request.FromDate.Date);
        var endUtc = AuditService.FromCAT(request.ToDate.Date.AddDays(1)).AddTicks(-1);

        var query = db.TimesheetEntries
            .AsNoTracking()
            .Where(t => t.Channel == TimesheetChannel.VanSales)
            .Where(t => t.CheckInTime >= startUtc && t.CheckInTime <= endUtc);

        if (request.UserId.HasValue)
            query = query.Where(t => t.UserId == request.UserId.Value);

        if (!string.IsNullOrWhiteSpace(request.Username))
            query = query.Where(t => t.Username == request.Username);

        // Materialised before grouping: the trading day is a CAT date and WasCapturedOffline is a
        // computed property, and neither survives translation to SQL.
        var entries = await query
            .OrderBy(t => t.CheckInTime)
            .Select(t => new ReportRow(
                t.UserId,
                t.Username,
                t.User != null ? ((t.User.FirstName ?? "") + " " + (t.User.LastName ?? "")).Trim() : null,
                t.CustomerCode,
                t.CustomerName,
                t.CheckInTime,
                t.CheckOutTime,
                t.DurationMinutes,
                t.CheckInRecordedAt,
                t.CheckOutRecordedAt))
            .ToListAsync(cancellationToken);

        var rounds = await RoundsAsync(entries, cancellationToken);

        var repSummaries = entries
            .GroupBy(row => new { row.UserId, row.Username })
            .Select(group => BuildRep(group.Key.UserId, group.Key.Username, group.ToList(), rounds))
            .OrderByDescending(rep => rep.TotalCalls)
            .ThenBy(rep => rep.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var completedCalls = entries.Count(row => row.CheckOutTime.HasValue);
        var totalMinutes = entries.Where(row => row.DurationMinutes.HasValue).Sum(row => row.DurationMinutes!.Value);

        return new VanVisitReportResult(
            request.FromDate.Date,
            request.ToDate.Date,
            repSummaries,
            entries.Count,
            completedCalls,
            entries.Count - completedCalls,
            entries.Count(row => row.WasCapturedOffline),
            totalMinutes / 60.0,
            // Averaged over the calls that closed. An open call has no duration, and counting it as
            // zero would drag the average down for a rep who simply has not finished the round.
            completedCalls > 0 ? totalMinutes / completedCalls : 0,
            entries.Select(row => TradingDay(row.CheckInTime)).Distinct().Count());
    }

    private static VanVisitReportRepSummary BuildRep(
        Guid userId,
        string username,
        List<ReportRow> rows,
        IReadOnlyDictionary<(Guid UserId, DateTime Day), Round> rounds)
    {
        var days = rows
            .GroupBy(row => TradingDay(row.CheckInTime))
            .Select(group =>
            {
                rounds.TryGetValue((userId, group.Key), out var round);

                return new VanVisitReportDaySummary(
                    group.Key,
                    group.Count(),
                    group.Select(row => row.CustomerCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    group.Count(row => !row.CheckOutTime.HasValue),
                    group.Where(row => row.DurationMinutes.HasValue).Sum(row => row.DurationMinutes!.Value),
                    group.Min(row => row.CheckInTime),
                    group.Where(row => row.CheckOutTime.HasValue).Max(row => row.CheckOutTime),
                    // In check-in order, which is the order the page draws them in. The outer query
                    // is already ordered by check-in time, so this is the grouping's own order.
                    group
                        .Select(row => new VanVisitReportCallSummary(
                            row.CustomerCode,
                            row.CustomerName,
                            row.CheckInTime,
                            row.CheckOutTime))
                        .ToList(),
                    round?.Code,
                    round?.Name);
            })
            .OrderByDescending(day => day.Date)
            .ToList();

        // The latest day that names a route, not the first one found: `days` is newest first, so this
        // is the route the rep is on now. A rep moved between routes mid-period is listed under the
        // one they are running today, while each of their days keeps the route it was actually run on.
        var latestRound = days.FirstOrDefault(day => day.RouteCode is not null || day.RouteName is not null);

        var customers = rows
            .GroupBy(row => row.CustomerCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanVisitReportCustomerSummary(
                group.Key,
                group.First().CustomerName,
                group.Count(),
                group.Where(row => row.DurationMinutes.HasValue).Sum(row => row.DurationMinutes!.Value)))
            .OrderByDescending(customer => customer.CallCount)
            .ThenBy(customer => customer.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var completedCalls = rows.Count(row => row.CheckOutTime.HasValue);
        var totalMinutes = rows.Where(row => row.DurationMinutes.HasValue).Sum(row => row.DurationMinutes!.Value);

        return new VanVisitReportRepSummary(
            userId,
            username,
            rows.Select(row => row.FullName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
            rows.Count,
            completedCalls,
            rows.Count - completedCalls,
            rows.Count(row => row.WasCapturedOffline),
            customers.Count,
            days.Count,
            totalMinutes,
            completedCalls > 0 ? totalMinutes / completedCalls : 0,
            days,
            customers,
            latestRound?.RouteCode,
            latestRound?.RouteName);
    }

    /// <summary>
    /// The route and truck each rep-day's round ran on, keyed by rep and CAT trading day.
    ///
    /// A second query rather than a join, for the reason <c>GetVanVisitsHandler</c> gives for the
    /// same read: the key is the CAT trading day of the check-in, and that arithmetic belongs in
    /// <see cref="AuditService.ToCAT"/> rather than in whatever the provider makes of
    /// <c>AddHours(2).Date</c>. Bounded by the days actually present in the report, so a wide date
    /// filter over a quiet period stays a small indexed read.
    /// </summary>
    private async Task<Dictionary<(Guid UserId, DateTime Day), Round>> RoundsAsync(
        List<ReportRow> entries,
        CancellationToken cancellationToken)
    {
        var byRound = new Dictionary<(Guid UserId, DateTime Day), Round>();
        if (entries.Count == 0) return byRound;

        var userIds = entries.Select(entry => entry.UserId).Distinct().ToList();
        var days = entries.Select(entry => TradingDay(entry.CheckInTime)).ToList();
        var firstDay = days.Min();
        var lastDay = days.Max();

        var rounds = await db.VanRouteDays
            .AsNoTracking()
            .Where(day => userIds.Contains(day.UserId)
                          && day.TradingDate >= firstDay
                          && day.TradingDate <= lastDay)
            .Select(day => new
            {
                day.UserId,
                day.TradingDate,
                day.RouteCode,
                day.RouteName
            })
            .ToListAsync(cancellationToken);

        // Built with an overwrite rather than ToDictionary: the unique index on
        // (UserId, TradingDate) makes this one row per key, and ToDictionary would answer a breach of
        // that index by throwing and taking the whole report down with it.
        foreach (var round in rounds)
        {
            byRound[(round.UserId, round.TradingDate.Date)] = new Round(round.RouteCode, round.RouteName);
        }

        return byRound;
    }

    private sealed record Round(string? Code, string? Name);

    /// <summary>
    /// The CAT calendar date a call belongs to. Zimbabwe has never observed daylight saving, so the
    /// fixed offset the rest of the app uses is correct here too.
    /// </summary>
    private static DateTime TradingDay(DateTime checkInUtc) => AuditService.ToCAT(checkInUtc).Date;

    /// <summary>
    /// The columns this report reads, projected in SQL so the whole entity is not dragged back.
    /// <see cref="WasCapturedOffline"/> mirrors
    /// <see cref="TimesheetEntryEntity.WasCapturedOffline"/> and shares its threshold.
    /// </summary>
    private sealed record ReportRow(
        Guid UserId,
        string Username,
        string? FullName,
        string CustomerCode,
        string CustomerName,
        DateTime CheckInTime,
        DateTime? CheckOutTime,
        double? DurationMinutes,
        DateTime? CheckInRecordedAt,
        DateTime? CheckOutRecordedAt)
    {
        public bool WasCapturedOffline =>
            IsLate(CheckInTime, CheckInRecordedAt) || IsLate(CheckOutTime, CheckOutRecordedAt);

        private static bool IsLate(DateTime? occurred, DateTime? recorded) =>
            occurred.HasValue && recorded.HasValue &&
            recorded.Value - occurred.Value > TimeSpan.FromMinutes(2);
    }
}
