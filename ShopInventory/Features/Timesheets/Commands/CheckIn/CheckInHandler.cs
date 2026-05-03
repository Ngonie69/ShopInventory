using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Timesheets.Commands.CheckIn;

public sealed class CheckInHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<CheckInHandler> logger
) : IRequestHandler<CheckInCommand, ErrorOr<CheckInResult>>
{
    public async Task<ErrorOr<CheckInResult>> Handle(
        CheckInCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var hasActiveCheckIn = await db.TimesheetEntries
                .AnyAsync(t => t.UserId == command.UserId && t.CheckOutTime == null, cancellationToken);

            if (hasActiveCheckIn)
                return Errors.Timesheet.AlreadyCheckedIn;

            var entry = new TimesheetEntryEntity
            {
                UserId = command.UserId,
                Username = command.Username,
                CustomerCode = command.CustomerCode,
                CustomerName = command.CustomerName,
                CheckInTime = DateTime.UtcNow,
                CheckInLatitude = command.Latitude,
                CheckInLongitude = command.Longitude,
                CheckInNotes = command.Notes
            };

            db.TimesheetEntries.Add(entry);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("User {Username} checked in at {CustomerCode} ({CustomerName})",
                command.Username, command.CustomerCode, command.CustomerName);

            try
            {
                await auditService.LogAsync(
                    AuditActions.CheckIn,
                    "Timesheet",
                    entry.Id.ToString(),
                    $"Checked in at {entry.CustomerCode} ({entry.CustomerName})",
                    true);
            }
            catch
            {
            }

            return new CheckInResult(entry.Id, entry.CheckInTime, entry.CustomerCode, entry.CustomerName, entry.CheckInLatitude, entry.CheckInLongitude);
        }
        catch (DbUpdateException ex) when (IsActiveCheckInConstraintViolation(ex))
        {
            logger.LogInformation("Concurrent check-in prevented for user {Username}", command.Username);
            return Errors.Timesheet.AlreadyCheckedIn;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking in user {Username} at {CustomerCode}", command.Username, command.CustomerCode);
            return Errors.Timesheet.CheckInFailed(ex.Message);
        }
    }

    private static bool IsActiveCheckInConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { ConstraintName: "IX_TimesheetEntries_UserId_ActiveCheckIn" };
    }
}
