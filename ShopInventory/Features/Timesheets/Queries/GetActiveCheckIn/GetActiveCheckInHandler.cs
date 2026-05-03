using ErrorOr;
using MediatR;
using ShopInventory.Data;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Features.Timesheets.Queries.GetActiveCheckIn;

public sealed class GetActiveCheckInHandler(
    ApplicationDbContext db
) : IRequestHandler<GetActiveCheckInQuery, ErrorOr<ActiveCheckInResult>>
{
    public async Task<ErrorOr<ActiveCheckInResult>> Handle(
        GetActiveCheckInQuery request,
        CancellationToken cancellationToken)
    {
        var entry = await db.TimesheetEntries
            .AsNoTracking()
            .Where(t => t.UserId == request.UserId && t.CheckOutTime == null)
            .OrderByDescending(t => t.CheckInTime)
            .ThenByDescending(t => t.Id)
            .Select(t => new ActiveCheckInResult(
                t.Id,
                t.CustomerCode,
                t.CustomerName,
                t.CheckInTime,
                t.CheckInLatitude,
                t.CheckInLongitude,
                t.CheckInNotes))
            .FirstOrDefaultAsync(cancellationToken);

        if (entry is null)
            return Common.Errors.Errors.Timesheet.NoActiveCheckIn;

        return entry;
    }
}
