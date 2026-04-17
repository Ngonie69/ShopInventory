using ErrorOr;
using MediatR;
using ShopInventory.Data;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Features.Timesheets.Queries.GetTimesheets;

public sealed class GetTimesheetsHandler(
    ApplicationDbContext db
) : IRequestHandler<GetTimesheetsQuery, ErrorOr<TimesheetListResult>>
{
    public async Task<ErrorOr<TimesheetListResult>> Handle(
        GetTimesheetsQuery request,
        CancellationToken cancellationToken)
    {
        var query = db.TimesheetEntries.AsNoTracking().AsQueryable();

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
            .Select(t => new TimesheetEntryDto(
                t.Id,
                t.UserId,
                t.Username,
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
                t.DurationMinutes))
            .ToListAsync(cancellationToken);

        return new TimesheetListResult(entries, totalCount, request.Page, request.PageSize);
    }
}
