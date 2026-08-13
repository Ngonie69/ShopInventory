using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisits;

public sealed class GetVanVisitsHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanVisitsQuery, ErrorOr<VanVisitListResult>>
{
    public async Task<ErrorOr<VanVisitListResult>> Handle(
        GetVanVisitsQuery request,
        CancellationToken cancellationToken)
    {
        // Pinned, not filtered. No caller can widen this to merchandiser rows.
        var query = db.TimesheetEntries
            .AsNoTracking()
            .Where(t => t.Channel == TimesheetChannel.VanSales);

        if (request.UserId.HasValue)
            query = query.Where(t => t.UserId == request.UserId.Value);

        if (!string.IsNullOrWhiteSpace(request.Username))
            query = query.Where(t => t.Username == request.Username);

        if (!string.IsNullOrWhiteSpace(request.CustomerCode))
            query = query.Where(t => t.CustomerCode == request.CustomerCode);

        if (request.FromDate.HasValue)
            query = query.Where(t => t.CheckInTime >= request.FromDate.Value);

        if (request.ToDate.HasValue)
            query = query.Where(t => t.CheckInTime <= request.ToDate.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var entries = await query
            .OrderByDescending(t => t.CheckInTime)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(t => new VanVisitDto(
                t.Id,
                t.UserId,
                t.Username,
                t.User != null ? ((t.User.FirstName ?? "") + " " + (t.User.LastName ?? "")).Trim() : null,
                t.CustomerCode,
                t.CustomerName,
                t.CheckInTime,
                t.CheckOutTime,
                t.CheckInLatitude,
                t.CheckInLongitude,
                t.CheckOutLatitude,
                t.CheckOutLongitude,
                t.CheckInNotes,
                t.CheckOutNotes,
                t.DurationMinutes,
                t.CheckInLocationSource,
                t.CheckOutLocationSource,
                t.CheckInLocationAccuracyMetres,
                t.CheckOutLocationAccuracyMetres,
                t.LocationUnavailableReason,
                t.CheckInRecordedAt,
                t.CheckOutRecordedAt))
            .ToListAsync(cancellationToken);

        return new VanVisitListResult(
            await WithRoundsAsync(entries, cancellationToken),
            totalCount,
            request.Page,
            request.PageSize);
    }

    /// <summary>
    /// Stamp each call with the route and truck its round ran on.
    ///
    /// A second query over the page rather than a join, because the key is the CAT trading day of the
    /// check-in and that arithmetic belongs in <see cref="AuditService.ToCAT"/> rather than in
    /// whatever the provider translates <c>AddHours(2).Date</c> into. The page is at most a few
    /// hundred rows and collapses to a handful of rep-days, so this is one small indexed read.
    /// </summary>
    private async Task<List<VanVisitDto>> WithRoundsAsync(
        List<VanVisitDto> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return entries;

        var userIds = entries.Select(entry => entry.UserId).Distinct().ToList();
        var days = entries.Select(entry => AuditService.ToCAT(entry.CheckInTime).Date).ToList();
        var firstDay = days.Min();
        var lastDay = days.Max();

        // Bounded by the page's own span, so a wide date filter cannot turn this into a table scan.
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
                day.RouteName,
                day.TruckRegNo
            })
            .ToListAsync(cancellationToken);

        if (rounds.Count == 0) return entries;

        // The unique index on (UserId, TradingDate) makes this one row per key, so a dictionary is
        // safe — but built with an overwrite rather than ToDictionary, which would throw on the
        // duplicate this index is supposed to prevent and take the whole page down with it.
        var byRound = new Dictionary<(Guid UserId, DateTime Day), (string? Code, string? Name, string? Truck)>();
        foreach (var round in rounds)
        {
            byRound[(round.UserId, round.TradingDate.Date)] = (round.RouteCode, round.RouteName, round.TruckRegNo);
        }

        return entries
            .Select(entry => byRound.TryGetValue(
                (entry.UserId, AuditService.ToCAT(entry.CheckInTime).Date), out var round)
                ? entry with
                {
                    RouteCode = round.Code,
                    RouteName = round.Name,
                    TruckRegNo = round.Truck
                }
                : entry)
            .ToList();
    }
}
