using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomers;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.StartVanSalesDay;

/// <summary>
/// Opens a rep's trading day: the "Departure compliance" half of the sheet.
///
/// Deliberately forgiving about being called twice. A rep taps Start Day at a depot with no signal,
/// the request queues, and the handset may well send it again — so a second call for a day that is
/// already open returns that day rather than failing. What it will not do is reopen a day the rep has
/// already closed, because the closing mileage and the declared cash would then be describing a
/// departure that came after them.
/// </summary>
public sealed class StartVanSalesDayHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IAuditService auditService,
    ILogger<StartVanSalesDayHandler> logger
) : IRequestHandler<StartVanSalesDayCommand, ErrorOr<VanSalesRouteDayResponse>>
{
    public async Task<ErrorOr<VanSalesRouteDayResponse>> Handle(
        StartVanSalesDayCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var user = await db.Users
            .AsNoTracking()
            .Include(candidate => candidate.Route)
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
                .FirstOrDefaultAsync(d => d.StartClientReference == clientReference, cancellationToken);

            if (replayed is not null)
            {
                return VanSalesRouteDayMapper.Map(replayed, "Day already started.");
            }
        }

        var recordedAt = DateTime.UtcNow;
        var departedAt = CaptureClock.Resolve(CaptureClock.Parse(request.CapturedAt), recordedAt);

        // The trading day is the CAT date of the departure, not of the request. A van leaving at 05:30
        // and a handset syncing at 21:00 must land on the same day, and only the departure says which.
        var tradingDate = AuditService.ToCAT(departedAt).Date;

        var existing = await db.VanRouteDays
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.UserId == user.Id && d.TradingDate == tradingDate,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.IsClosed)
            {
                return Error.Conflict(
                    "VanSalesCompatibility.DayAlreadyClosed",
                    $"The day of {tradingDate:dd MMM yyyy} has already been closed.");
            }

            return VanSalesRouteDayMapper.Map(existing, "Day already started.");
        }

        var plannedCustomerCount = await CountPlannedCustomersAsync(user.Id, cancellationToken);

        var day = new VanRouteDayEntity
        {
            UserId = user.Id,
            Username = user.Username,
            TradingDate = tradingDate,

            RouteId = user.Route?.Id,
            RouteCode = user.Route?.Code,
            RouteName = user.Route?.Name,
            Territory = user.Route?.Territory,

            // What the rep confirmed on the handset, falling back to the route's registered vehicle.
            TruckRegNo = NullIfBlank(request.TruckRegNo) ?? user.Route?.TruckRegNo,

            DepartedAt = departedAt,
            DepartedRecordedAt = recordedAt,
            DepartedLatitude = ParseCoordinate(request.Latitude),
            DepartedLongitude = ParseCoordinate(request.Longitude),
            StartingMileage = request.StartingMileage,
            PlannedCustomerCount = plannedCustomerCount,
            RtiOut = request.RtiOut,

            Notes = Truncate(request.Notes, 1000),
            StartClientReference = clientReference
        };

        db.VanRouteDays.Add(day);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateDay(ex))
        {
            // Two queued starts racing. Neither had committed when the lookup above ran, so the loser
            // reads the winner's row instead of failing a tap the rep already made.
            db.Entry(day).State = EntityState.Detached;

            var winner = await db.VanRouteDays
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    d => d.UserId == user.Id && d.TradingDate == tradingDate,
                    cancellationToken);

            return winner is not null
                ? VanSalesRouteDayMapper.Map(winner, "Day already started.")
                : Error.Failure("VanSalesCompatibility.StartDayFailed", ex.Message);
        }

        logger.LogInformation(
            "Van day opened for {Username} on route {RouteCode} at {DepartedAt:O}, {PlannedCustomers} customers planned",
            user.Username, day.RouteCode ?? "(none)", departedAt, plannedCustomerCount);

        await LogAsync(day, "Started the trading day");

        return VanSalesRouteDayMapper.Map(day, "Day started.");
    }

    /// <summary>
    /// How many customers the route holds right now — the CCR denominator, frozen onto the day.
    ///
    /// Asks the same query the handset's own customer list comes from rather than counting route
    /// customers directly, so the denominator can never be a different set from the shops the rep is
    /// actually shown. That query already handles both the route-customer vans and the ones assigned
    /// real business partners.
    /// </summary>
    private async Task<int> CountPlannedCustomersAsync(Guid userId, CancellationToken cancellationToken)
    {
        var shops = await mediator.Send(new GetVanSalesCustomersQuery(userId), cancellationToken);

        if (shops.IsError)
        {
            // A day with no denominator is still a day worth recording; the report shows the CCR as
            // unavailable rather than dividing by a guess.
            logger.LogWarning(
                "Could not count planned customers for {UserId}: {Error}",
                userId, shops.FirstError.Description);

            return 0;
        }

        return shops.Value.Count;
    }

    private async Task LogAsync(VanRouteDayEntity day, string what)
    {
        try
        {
            await auditService.LogAsync(
                "VanRouteDayStart",
                "VanRouteDay",
                day.Id.ToString(),
                $"{what} on {day.TradingDate:dd MMM yyyy} for route {day.RouteName ?? day.RouteCode ?? "(none)"}.",
                true);
        }
        catch
        {
        }
    }

    private static bool IsDuplicateDay(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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
