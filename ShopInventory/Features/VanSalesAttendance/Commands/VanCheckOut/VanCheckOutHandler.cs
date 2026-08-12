using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesAttendance.Commands.VanCheckOut;

/// <summary>
/// Closes a van sales call. Every read is pinned to <see cref="TimesheetChannel.VanSales"/>, so this
/// can never close a merchandiser's open visit — see <see cref="VanCheckIn.VanCheckInHandler"/>.
/// </summary>
public sealed class VanCheckOutHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<VanCheckOutHandler> logger
) : IRequestHandler<VanCheckOutCommand, ErrorOr<VanCheckOutResult>>
{
    public async Task<ErrorOr<VanCheckOutResult>> Handle(
        VanCheckOutCommand command,
        CancellationToken cancellationToken)
    {
        var capture = command.Capture ?? CaptureContext.Live;

        try
        {
            // A replayed check-out. Same reasoning as the check-in side: the queued record is resent
            // when a sync loses its reply, and closing an unrelated later call would be worse than
            // doing nothing.
            if (!string.IsNullOrWhiteSpace(capture.ClientReference))
            {
                var alreadyClosed = await db.TimesheetEntries
                    .AsNoTracking()
                    .Where(t => t.Channel == TimesheetChannel.VanSales)
                    .FirstOrDefaultAsync(
                        t => t.CheckOutClientReference == capture.ClientReference,
                        cancellationToken);

                if (alreadyClosed is not null)
                {
                    logger.LogInformation(
                        "Replayed van check-out {ClientReference} for {Username}; returning entry {EntryId}",
                        capture.ClientReference, command.Username, alreadyClosed.Id);

                    return ToResult(alreadyClosed, wasReplay: true);
                }
            }

            var activeEntries = await db.TimesheetEntries
                .AsTracking()
                .Where(t => t.UserId == command.UserId
                            && t.Channel == TimesheetChannel.VanSales
                            && t.CheckOutTime == null)
                .OrderByDescending(t => t.CheckInTime)
                .ThenByDescending(t => t.Id)
                .ToListAsync(cancellationToken);

            if (activeEntries.Count == 0)
                return Errors.Timesheet.NoActiveCheckIn;

            var entry = activeEntries[0];
            var duplicateEntries = activeEntries.Skip(1).ToList();

            var recordedAt = DateTime.UtcNow;
            var checkOutTime = CaptureClock.Resolve(capture.OccurredAt, recordedAt);

            // A queued check-out can only be believed as far as the check-in it closes. A clock that
            // ran backwards, or a pair replayed out of order, would otherwise write a negative
            // duration — which is not merely odd, it subtracts time from the day's total.
            if (checkOutTime < entry.CheckInTime)
            {
                logger.LogWarning(
                    "Van check-out for entry {EntryId} claimed {Claimed:O}, before its check-in at {CheckIn:O}; using the check-in time",
                    entry.Id, checkOutTime, entry.CheckInTime);

                checkOutTime = entry.CheckInTime;
            }

            entry.CheckOutTime = checkOutTime;
            entry.CheckOutRecordedAt = recordedAt;
            entry.CheckOutLatitude = command.Latitude;
            entry.CheckOutLongitude = command.Longitude;
            entry.CheckOutNotes = command.Notes;
            entry.CheckOutClientReference = NullIfBlank(capture.ClientReference);
            entry.CheckOutLocationSource = NullIfBlank(capture.LocationSource);
            entry.CheckOutLocationAccuracyMetres = capture.AccuracyMetres;
            entry.DurationMinutes = (checkOutTime - entry.CheckInTime).TotalMinutes;

            if (Truncate(capture.LocationUnavailableReason, 200) is { } reason)
            {
                entry.LocationUnavailableReason = reason;
            }

            foreach (var duplicateEntry in duplicateEntries)
            {
                duplicateEntry.CheckOutTime = duplicateEntry.CheckInTime;
                duplicateEntry.CheckOutRecordedAt = recordedAt;
                duplicateEntry.DurationMinutes = 0;
                duplicateEntry.CheckOutNotes = AppendDuplicateCloseNote(duplicateEntry.CheckOutNotes);
            }

            await db.SaveChangesAsync(cancellationToken);

            if (duplicateEntries.Count > 0)
            {
                logger.LogWarning(
                    "Closed {DuplicateCount} duplicate active van check-ins for user {Username} while checking out entry {EntryId}",
                    duplicateEntries.Count,
                    command.Username,
                    entry.Id);
            }

            logger.LogInformation("Van rep {Username} checked out from {CustomerCode} after {Duration:F1} minutes",
                command.Username, entry.CustomerCode, entry.DurationMinutes);

            try
            {
                var duplicateSuffix = duplicateEntries.Count > 0
                    ? $" Duplicate open calls closed: {duplicateEntries.Count}."
                    : string.Empty;

                var lateSuffix = entry.WasCapturedOffline
                    ? $" Captured offline at {AuditService.ToCAT(checkOutTime):dd MMM HH:mm} and synced later."
                    : string.Empty;

                await auditService.LogAsync(
                    AuditActions.CheckOut,
                    "VanSalesAttendance",
                    entry.Id.ToString(),
                    $"Van checked out from {entry.CustomerCode} ({entry.CustomerName}) after {entry.DurationMinutes:F1} minutes.{duplicateSuffix}{lateSuffix}",
                    true);
            }
            catch
            {
            }

            return ToResult(entry, wasReplay: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking out van rep {Username}", command.Username);
            return Errors.Timesheet.CheckOutFailed(ex.Message);
        }
    }

    private static VanCheckOutResult ToResult(TimesheetEntryEntity entry, bool wasReplay) => new(
        entry.Id,
        entry.CustomerCode,
        entry.CustomerName,
        entry.CheckInTime,
        entry.CheckOutTime ?? entry.CheckInTime,
        entry.DurationMinutes ?? 0,
        entry.CheckOutLatitude,
        entry.CheckOutLongitude,
        wasReplay);

    private static string AppendDuplicateCloseNote(string? existingNotes)
    {
        const string note = "Auto-closed duplicate active check-in.";

        var combined = string.IsNullOrWhiteSpace(existingNotes)
            ? note
            : $"{existingNotes.Trim()} | {note}";

        return combined.Length <= 500 ? combined : combined[..500];
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = NullIfBlank(value);
        return trimmed is null || trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
