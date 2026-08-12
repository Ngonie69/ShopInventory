using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesAttendance.Queries.GetActiveVanVisit;

public sealed class GetActiveVanVisitHandler(
    ApplicationDbContext db
) : IRequestHandler<GetActiveVanVisitQuery, ErrorOr<ActiveVanVisitResult>>
{
    public async Task<ErrorOr<ActiveVanVisitResult>> Handle(
        GetActiveVanVisitQuery request,
        CancellationToken cancellationToken)
    {
        var entry = await db.TimesheetEntries
            .AsNoTracking()
            .Where(t => t.UserId == request.UserId
                        && t.Channel == TimesheetChannel.VanSales
                        && t.CheckOutTime == null)
            .OrderByDescending(t => t.CheckInTime)
            .ThenByDescending(t => t.Id)
            .Select(t => new ActiveVanVisitResult(
                t.Id,
                t.CustomerCode,
                t.CustomerName,
                t.CheckInTime,
                t.CheckInLatitude,
                t.CheckInLongitude,
                t.CheckInNotes))
            .FirstOrDefaultAsync(cancellationToken);

        if (entry is null)
            return Errors.Timesheet.NoActiveCheckIn;

        return entry;
    }
}
