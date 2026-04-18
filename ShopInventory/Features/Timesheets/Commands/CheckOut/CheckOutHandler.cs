using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Features.Timesheets.Commands.CheckOut;

public sealed class CheckOutHandler(
    ApplicationDbContext db,
    ILogger<CheckOutHandler> logger
) : IRequestHandler<CheckOutCommand, ErrorOr<CheckOutResult>>
{
    public async Task<ErrorOr<CheckOutResult>> Handle(
        CheckOutCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await db.TimesheetEntries
                .AsTracking()
                .FirstOrDefaultAsync(t => t.UserId == command.UserId && t.CheckOutTime == null, cancellationToken);

            if (entry is null)
                return Errors.Timesheet.NoActiveCheckIn;

            var checkOutTime = DateTime.UtcNow;
            entry.CheckOutTime = checkOutTime;
            entry.CheckOutLatitude = command.Latitude;
            entry.CheckOutLongitude = command.Longitude;
            entry.CheckOutNotes = command.Notes;
            entry.DurationMinutes = (checkOutTime - entry.CheckInTime).TotalMinutes;

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("User {Username} checked out from {CustomerCode} after {Duration:F1} minutes",
                command.Username, entry.CustomerCode, entry.DurationMinutes);

            return new CheckOutResult(
                entry.Id,
                entry.CustomerCode,
                entry.CustomerName,
                entry.CheckInTime,
                checkOutTime,
                entry.DurationMinutes.Value,
                entry.CheckOutLatitude,
                entry.CheckOutLongitude);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking out user {Username}", command.Username);
            return Errors.Timesheet.CheckOutFailed(ex.Message);
        }
    }
}
