using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.EndVanSalesDay;

/// <summary>
/// Closes a rep's trading day: closing mileage, the RTI brought back, and the takings counted.
///
/// Closes the most recent day still open rather than the day the return time falls in. A round that
/// finishes at 00:40 is Tuesday's work and the rep says so by having started it on Tuesday; picking
/// the day by the clock would file the closing mileage against a Wednesday that has not begun.
/// </summary>
public sealed class EndVanSalesDayHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<EndVanSalesDayHandler> logger
) : IRequestHandler<EndVanSalesDayCommand, ErrorOr<VanSalesRouteDayResponse>>
{
    /// <summary>
    /// How far back to look for the day being closed. Generous enough for a handset that has been out
    /// of coverage for a long weekend, short enough that a rep who forgot to close last month does not
    /// have this evening's mileage written onto it.
    /// </summary>
    private static readonly TimeSpan OpenDayLookback = TimeSpan.FromDays(4);

    public async Task<ErrorOr<VanSalesRouteDayResponse>> Handle(
        EndVanSalesDayCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var clientReference = NullIfBlank(request.ClientReference);

        if (clientReference is not null)
        {
            var replayed = await db.VanRouteDays
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.EndClientReference == clientReference, cancellationToken);

            if (replayed is not null)
            {
                return VanSalesRouteDayMapper.Map(replayed, "Day already closed.");
            }
        }

        var recordedAt = DateTime.UtcNow;
        var returnedAt = CaptureClock.Resolve(CaptureClock.Parse(request.CapturedAt), recordedAt);
        var earliestTradingDate = AuditService.ToCAT(returnedAt).Date - OpenDayLookback;

        var day = await db.VanRouteDays
            .AsTracking()
            .Where(d => d.UserId == user.Id
                        && d.ReturnedAt == null
                        && d.TradingDate >= earliestTradingDate)
            .OrderByDescending(d => d.TradingDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (day is null)
        {
            return Error.NotFound(
                "VanSalesCompatibility.NoOpenDay",
                "There is no open trading day to close. Start the day before ending it.");
        }

        // The return cannot precede the departure. A handset clock that ran backwards would otherwise
        // produce a negative day length, which reads as a data error to a person and as a valid
        // subtraction to a report.
        if (returnedAt < day.DepartedAt)
        {
            logger.LogWarning(
                "Van day {DayId} claimed a return at {Returned:O}, before its departure at {Departed:O}; using the departure time",
                day.Id, returnedAt, day.DepartedAt);

            returnedAt = day.DepartedAt;
        }

        if (request.ClosingMileage is { } closing && day.StartingMileage is { } starting && closing < starting)
        {
            return Error.Validation(
                "VanSalesCompatibility.ClosingMileageBeforeStart",
                $"Closing mileage ({closing:N0}) cannot be less than the starting mileage ({starting:N0}).");
        }

        day.ReturnedAt = returnedAt;
        day.ReturnedRecordedAt = recordedAt;
        day.ReturnedLatitude = ParseCoordinate(request.Latitude);
        day.ReturnedLongitude = ParseCoordinate(request.Longitude);
        day.ClosingMileage = request.ClosingMileage;
        day.RtiReturned = request.RtiReturned;
        day.DeclaredCash = request.DeclaredCash;
        day.DeclaredEcocash = request.DeclaredEcocash;
        day.DeclaredInnbucks = request.DeclaredInnbucks;
        day.DeclaredCurrency = NullIfBlank(request.DeclaredCurrency);
        day.EndClientReference = clientReference;
        day.UpdatedAt = recordedAt;

        if (Truncate(request.Notes, 1000) is { } notes)
        {
            day.Notes = string.IsNullOrWhiteSpace(day.Notes) ? notes : $"{day.Notes} | {notes}";
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Van day {DayId} closed for {Username}: {Kilometres} km, declared {Declared}",
            day.Id, user.Username, day.KilometresTravelled?.ToString() ?? "?", day.DeclaredTotal?.ToString("F2") ?? "nothing");

        try
        {
            await auditService.LogAsync(
                "VanRouteDayEnd",
                "VanRouteDay",
                day.Id.ToString(),
                $"Closed the trading day of {day.TradingDate:dd MMM yyyy}: " +
                $"{day.KilometresTravelled?.ToString("N0") ?? "unknown"} km, " +
                $"declared {day.DeclaredTotal?.ToString("N2") ?? "nothing"}.",
                true);
        }
        catch
        {
        }

        return VanSalesRouteDayMapper.Map(day, "Day closed.");
    }

    private static double? ParseCoordinate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = NullIfBlank(value);
        return trimmed is null || trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
