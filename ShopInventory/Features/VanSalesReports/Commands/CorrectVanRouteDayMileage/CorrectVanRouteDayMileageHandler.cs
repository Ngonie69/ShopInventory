using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.CorrectVanRouteDayMileage;

/// <summary>
/// Corrects a trading day's odometer readings in place and keeps a row saying it was done.
/// </summary>
/// <remarks>
/// In place, because every report (compliance, the daily truck report, the fleet audit, the handset's
/// own current-day read) takes the readings from the day, and a correction anywhere else would have
/// to be taught to each of them. The trail is in <see cref="VanRouteDayMileageCorrectionEntity"/>.
/// </remarks>
public sealed class CorrectVanRouteDayMileageHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<CorrectVanRouteDayMileageHandler> logger
) : IRequestHandler<CorrectVanRouteDayMileageCommand, ErrorOr<VanRouteDayMileageDto>>
{
    /// <summary>
    /// Seven digits: more than any odometer in the fleet reads, and low enough to catch a reading
    /// typed with a digit too many.
    /// </summary>
    public const int MaximumReading = 9_999_999;

    public const int MaximumReasonLength = 500;

    public async Task<ErrorOr<VanRouteDayMileageDto>> Handle(
        CorrectVanRouteDayMileageCommand command,
        CancellationToken cancellationToken)
    {
        var reason = command.Reason?.Trim();

        if (string.IsNullOrEmpty(reason))
        {
            return Errors.VanRouteDayMileage.ReasonRequired;
        }

        if (reason.Length > MaximumReasonLength)
        {
            reason = reason[..MaximumReasonLength];
        }

        if (command.StartingMileage is < 0 or > MaximumReading
            || command.ClosingMileage is < 0 or > MaximumReading)
        {
            return Errors.VanRouteDayMileage.ReadingOutOfRange(MaximumReading);
        }

        if (command.StartingMileage is { } starting
            && command.ClosingMileage is { } closing
            && closing < starting)
        {
            return Errors.VanRouteDayMileage.ClosingBeforeStarting(starting, closing);
        }

        var day = await db.VanRouteDays
            .AsTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.VanRouteDayId, cancellationToken);

        if (day is null)
        {
            return Errors.VanRouteDayMileage.DayNotFound;
        }

        if (day.StartingMileage != command.ExpectedStartingMileage
            || day.ClosingMileage != command.ExpectedClosingMileage)
        {
            return Errors.VanRouteDayMileage.Stale(day.StartingMileage, day.ClosingMileage);
        }

        if (day.StartingMileage == command.StartingMileage && day.ClosingMileage == command.ClosingMileage)
        {
            return Errors.VanRouteDayMileage.NothingChanged;
        }

        if (!day.IsClosed && command.ClosingMileage != day.ClosingMileage)
        {
            return Errors.VanRouteDayMileage.DayStillOpen;
        }

        var actor = command.ActingUserId is { } actorId
            ? await db.Users
                .AsNoTracking()
                .Where(user => user.Id == actorId)
                .Select(user => user.Username)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var now = DateTime.UtcNow;
        var correction = new VanRouteDayMileageCorrectionEntity
        {
            VanRouteDayId = day.Id,
            PreviousStartingMileage = day.StartingMileage,
            PreviousClosingMileage = day.ClosingMileage,
            StartingMileage = command.StartingMileage,
            ClosingMileage = command.ClosingMileage,
            Reason = reason,
            // An actor the Users table does not hold is the API key on its own, which has no account
            // for the foreign key to point at. The name is still worth keeping.
            CorrectedByUserId = actor is null ? null : command.ActingUserId,
            CorrectedByName = actor,
            CorrectedAt = now
        };

        day.StartingMileage = command.StartingMileage;
        day.ClosingMileage = command.ClosingMileage;
        day.UpdatedAt = now;
        db.VanRouteDayMileageCorrections.Add(correction);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Van day {DayId} odometer corrected by {Actor}: out {PreviousStart} -> {Start}, in {PreviousClose} -> {Close}",
            day.Id, actor ?? "the API key",
            correction.PreviousStartingMileage, correction.StartingMileage,
            correction.PreviousClosingMileage, correction.ClosingMileage);

        await LogAsync(day, correction);

        var trail = await db.VanRouteDayMileageCorrections
            .AsNoTracking()
            .Where(candidate => candidate.VanRouteDayId == day.Id)
            .ToListAsync(cancellationToken);

        return new VanRouteDayMileageDto(
            day.Id,
            day.StartingMileage,
            day.ClosingMileage,
            day.KilometresTravelled,
            MileageCorrectionSummary.From(trail, day.StartingMileage, day.ClosingMileage));
    }

    private async Task LogAsync(VanRouteDayEntity day, VanRouteDayMileageCorrectionEntity correction)
    {
        try
        {
            await auditService.LogAsync(
                "VanRouteDayMileageCorrected",
                "VanRouteDay",
                day.Id.ToString(),
                $"Corrected the odometer for {day.Username} on {day.TradingDate:dd MMM yyyy}: " +
                $"out {Show(correction.PreviousStartingMileage)} to {Show(correction.StartingMileage)}, " +
                $"in {Show(correction.PreviousClosingMileage)} to {Show(correction.ClosingMileage)}. " +
                $"Reason: {correction.Reason}",
                true);
        }
        catch
        {
        }
    }

    private static string Show(int? reading) => reading is { } value ? value.ToString("N0") : "blank";
}
