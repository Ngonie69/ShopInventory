using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

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

        return new VanVisitListResult(entries, totalCount, request.Page, request.PageSize);
    }
}
