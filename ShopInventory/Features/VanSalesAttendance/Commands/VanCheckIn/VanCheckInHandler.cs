using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesAttendance.Commands.VanCheckIn;

/// <summary>
/// Records a van sales call.
///
/// Deliberately a sibling of the merchandiser's <c>CheckInHandler</c> rather than a call into it. The
/// error codes are shared because they are a wire contract the handset already keys on, but nothing
/// else is: every query below is pinned to <see cref="TimesheetChannel.VanSales"/>, so this handler
/// can neither read nor close a merchandiser's visit, and a change made for the vans cannot reach the
/// merchandiser side by accident.
/// </summary>
public sealed class VanCheckInHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<VanCheckInHandler> logger
) : IRequestHandler<VanCheckInCommand, ErrorOr<VanCheckInResult>>
{
    public async Task<ErrorOr<VanCheckInResult>> Handle(
        VanCheckInCommand command,
        CancellationToken cancellationToken)
    {
        var capture = command.Capture ?? CaptureContext.Live;

        try
        {
            // A queued call that reached the server but lost its reply comes back on the next sync.
            // Answering with the row already stored is the whole point of the client reference: the
            // alternative is a second call at the same shop, which inflates the call count the
            // departure compliance report is built on.
            if (!string.IsNullOrWhiteSpace(capture.ClientReference))
            {
                var existing = await db.TimesheetEntries
                    .AsNoTracking()
                    .Where(t => t.Channel == TimesheetChannel.VanSales)
                    .FirstOrDefaultAsync(
                        t => t.CheckInClientReference == capture.ClientReference,
                        cancellationToken);

                if (existing is not null)
                {
                    logger.LogInformation(
                        "Replayed van check-in {ClientReference} for {Username}; returning entry {EntryId}",
                        capture.ClientReference, command.Username, existing.Id);

                    return ToResult(existing, wasReplay: true);
                }
            }

            var hasActiveCheckIn = await db.TimesheetEntries
                .AnyAsync(
                    t => t.UserId == command.UserId
                         && t.Channel == TimesheetChannel.VanSales
                         && t.CheckOutTime == null,
                    cancellationToken);

            if (hasActiveCheckIn)
                return Errors.Timesheet.AlreadyCheckedIn;

            var recordedAt = DateTime.UtcNow;

            var entry = new TimesheetEntryEntity
            {
                Channel = TimesheetChannel.VanSales,
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

            logger.LogInformation("Van rep {Username} checked in at {CustomerCode} ({CustomerName})",
                command.Username, command.CustomerCode, command.CustomerName);

            try
            {
                var lateSuffix = entry.WasCapturedOffline
                    ? $" Captured offline at {AuditService.ToCAT(entry.CheckInTime):dd MMM HH:mm} and synced later."
                    : string.Empty;

                await auditService.LogAsync(
                    AuditActions.CheckIn,
                    "VanSalesAttendance",
                    entry.Id.ToString(),
                    $"Van checked in at {entry.CustomerCode} ({entry.CustomerName}).{lateSuffix}",
                    true);
            }
            catch
            {
            }

            return ToResult(entry, wasReplay: false);
        }
        catch (DbUpdateException ex) when (IsActiveCheckInConstraintViolation(ex))
        {
            logger.LogInformation("Concurrent van check-in prevented for user {Username}", command.Username);
            return Errors.Timesheet.AlreadyCheckedIn;
        }
        catch (DbUpdateException ex) when (IsClientReferenceConstraintViolation(ex))
        {
            // Two syncs of the same queued call racing. The reference lookup above missed because
            // neither had committed yet; the loser reads the winner's row rather than failing.
            logger.LogInformation(
                "Concurrent replay of van check-in {ClientReference} for {Username}",
                capture.ClientReference, command.Username);

            var winner = await db.TimesheetEntries
                .AsNoTracking()
                .Where(t => t.Channel == TimesheetChannel.VanSales)
                .FirstOrDefaultAsync(
                    t => t.CheckInClientReference == capture.ClientReference,
                    cancellationToken);

            return winner is not null
                ? ToResult(winner, wasReplay: true)
                : Errors.Timesheet.CheckInFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking in van rep {Username} at {CustomerCode}",
                command.Username, command.CustomerCode);
            return Errors.Timesheet.CheckInFailed(ex.Message);
        }
    }

    private static VanCheckInResult ToResult(TimesheetEntryEntity entry, bool wasReplay) => new(
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
