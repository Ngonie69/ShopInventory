using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Mobile;
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
        var capture = command.Capture ?? CaptureContext.Live;

        try
        {
            // A queued visit that reached the server but lost its reply comes back on the next sync.
            // Answering with the row already stored is the whole point of the client reference: the
            // alternative is a second visit to the same shop, which inflates the call count the
            // compliance report is built on.
            if (!string.IsNullOrWhiteSpace(capture.ClientReference))
            {
                var existing = await db.TimesheetEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        t => t.CheckInClientReference == capture.ClientReference,
                        cancellationToken);

                if (existing is not null)
                {
                    logger.LogInformation(
                        "Replayed check-in {ClientReference} for {Username}; returning entry {EntryId}",
                        capture.ClientReference, command.Username, existing.Id);

                    return ToResult(existing, wasReplay: true);
                }
            }

            var hasActiveCheckIn = await db.TimesheetEntries
                .AnyAsync(t => t.UserId == command.UserId && t.CheckOutTime == null, cancellationToken);

            if (hasActiveCheckIn)
                return Errors.Timesheet.AlreadyCheckedIn;

            var recordedAt = DateTime.UtcNow;

            var entry = new TimesheetEntryEntity
            {
                Channel = command.Channel,
                UserId = command.UserId,
                Username = command.Username,
                CustomerCode = command.CustomerCode,
                CustomerName = command.CustomerName,

                // When the rep tapped, which offline is not when this request arrived.
                CheckInTime = CaptureClock.Resolve(capture.OccurredAt, recordedAt),
                CheckInRecordedAt = recordedAt,

                CheckInLatitude = command.Latitude,
                CheckInLongitude = command.Longitude,
                CheckInNotes = command.Notes,
                CheckInClientReference = NullIfBlank(capture.ClientReference),
                CheckInLocationSource = NullIfBlank(capture.LocationSource),
                CheckInLocationAccuracyMetres = capture.AccuracyMetres,
                LocationUnavailableReason = Truncate(capture.LocationUnavailableReason, 200)
            };

            db.TimesheetEntries.Add(entry);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("User {Username} checked in at {CustomerCode} ({CustomerName})",
                command.Username, command.CustomerCode, command.CustomerName);

            try
            {
                var lateSuffix = entry.WasCapturedOffline
                    ? $" Captured offline at {AuditService.ToCAT(entry.CheckInTime):dd MMM HH:mm} and synced later."
                    : string.Empty;

                await auditService.LogAsync(
                    AuditActions.CheckIn,
                    "Timesheet",
                    entry.Id.ToString(),
                    $"Checked in at {entry.CustomerCode} ({entry.CustomerName}).{lateSuffix}",
                    true);
            }
            catch
            {
            }

            return ToResult(entry, wasReplay: false);
        }
        catch (DbUpdateException ex) when (IsActiveCheckInConstraintViolation(ex))
        {
            logger.LogInformation("Concurrent check-in prevented for user {Username}", command.Username);
            return Errors.Timesheet.AlreadyCheckedIn;
        }
        catch (DbUpdateException ex) when (IsClientReferenceConstraintViolation(ex))
        {
            // Two syncs of the same queued visit racing. The reference lookup above missed because
            // neither had committed yet; the loser reads the winner's row rather than failing.
            logger.LogInformation(
                "Concurrent replay of check-in {ClientReference} for {Username}",
                capture.ClientReference, command.Username);

            var winner = await db.TimesheetEntries
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.CheckInClientReference == capture.ClientReference,
                    cancellationToken);

            return winner is not null
                ? ToResult(winner, wasReplay: true)
                : Errors.Timesheet.CheckInFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking in user {Username} at {CustomerCode}", command.Username, command.CustomerCode);
            return Errors.Timesheet.CheckInFailed(ex.Message);
        }
    }

    private static CheckInResult ToResult(TimesheetEntryEntity entry, bool wasReplay) => new(
        entry.Id,
        entry.CheckInTime,
        entry.CustomerCode,
        entry.CustomerName,
        entry.CheckInLatitude,
        entry.CheckInLongitude,
        wasReplay);

    private static bool IsActiveCheckInConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { ConstraintName: "IX_TimesheetEntries_UserId_ActiveCheckIn" };
    }

    private static bool IsClientReferenceConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            ConstraintName: "IX_TimesheetEntries_CheckInClientReference"
        };
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = NullIfBlank(value);
        return trimmed is null || trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
