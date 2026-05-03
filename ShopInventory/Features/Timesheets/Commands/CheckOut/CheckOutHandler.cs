using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Timesheets.Commands.CheckOut;

public sealed class CheckOutHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<CheckOutHandler> logger
) : IRequestHandler<CheckOutCommand, ErrorOr<CheckOutResult>>
{
    public async Task<ErrorOr<CheckOutResult>> Handle(
        CheckOutCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var activeEntries = await db.TimesheetEntries
                .AsTracking()
                .Where(t => t.UserId == command.UserId && t.CheckOutTime == null)
                .OrderByDescending(t => t.CheckInTime)
                .ThenByDescending(t => t.Id)
                .ToListAsync(cancellationToken);

            if (activeEntries.Count == 0)
                return Errors.Timesheet.NoActiveCheckIn;

            var entry = activeEntries[0];
            var duplicateEntries = activeEntries.Skip(1).ToList();

            var checkOutTime = DateTime.UtcNow;
            entry.CheckOutTime = checkOutTime;
            entry.CheckOutLatitude = command.Latitude;
            entry.CheckOutLongitude = command.Longitude;
            entry.CheckOutNotes = command.Notes;
            entry.DurationMinutes = (checkOutTime - entry.CheckInTime).TotalMinutes;

            foreach (var duplicateEntry in duplicateEntries)
            {
                duplicateEntry.CheckOutTime = duplicateEntry.CheckInTime;
                duplicateEntry.DurationMinutes = 0;
                duplicateEntry.CheckOutNotes = AppendDuplicateCloseNote(duplicateEntry.CheckOutNotes);
            }

            await db.SaveChangesAsync(cancellationToken);

            if (duplicateEntries.Count > 0)
            {
                logger.LogWarning(
                    "Closed {DuplicateCount} duplicate active check-ins for user {Username} while checking out entry {EntryId}",
                    duplicateEntries.Count,
                    command.Username,
                    entry.Id);
            }

            logger.LogInformation("User {Username} checked out from {CustomerCode} after {Duration:F1} minutes",
                command.Username, entry.CustomerCode, entry.DurationMinutes);

            try
            {
                var duplicateSuffix = duplicateEntries.Count > 0
                    ? $" Duplicate open visits closed: {duplicateEntries.Count}."
                    : string.Empty;

                await auditService.LogAsync(
                    AuditActions.CheckOut,
                    "Timesheet",
                    entry.Id.ToString(),
                    $"Checked out from {entry.CustomerCode} ({entry.CustomerName}) after {entry.DurationMinutes:F1} minutes.{duplicateSuffix}",
                    true);
            }
            catch
            {
            }

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

    private static string AppendDuplicateCloseNote(string? existingNotes)
    {
        const string note = "Auto-closed duplicate active check-in.";

        var combined = string.IsNullOrWhiteSpace(existingNotes)
            ? note
            : $"{existingNotes.Trim()} | {note}";

        return combined.Length <= 500 ? combined : combined[..500];
    }
}
