using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.SaveVanTruckDayEntry;

public sealed class SaveVanTruckDayEntryHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<SaveVanTruckDayEntryHandler> logger
) : IRequestHandler<SaveVanTruckDayEntryCommand, ErrorOr<VanTruckDayEntryDto>>
{
    /// <summary>
    /// More than any truck in the fleet is issued in a day, and low enough to catch a reading keyed
    /// into the wrong box: an odometer is six digits.
    /// </summary>
    public const decimal MaximumFuelLitres = 2000m;

    public async Task<ErrorOr<VanTruckDayEntryDto>> Handle(
        SaveVanTruckDayEntryCommand command,
        CancellationToken cancellationToken)
    {
        var day = command.TradingDate.Date;

        if (day > AuditService.ToCAT(DateTime.UtcNow).Date)
        {
            return Errors.VanTruckDays.DayInFuture;
        }

        if (command.FuelLitres is < 0 or > MaximumFuelLitres)
        {
            return Errors.VanTruckDays.FuelOutOfRange(MaximumFuelLitres);
        }

        if (command.AccountSales is < 0)
        {
            return Errors.VanTruckDays.AccountSalesNegative;
        }

        var route = await db.Routes
            .AsNoTracking()
            .Where(candidate => candidate.Id == command.RouteId && candidate.DeletedAt == null)
            .Select(candidate => new { candidate.Id, candidate.Code, candidate.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (route is null)
        {
            return Errors.VanTruckDays.RouteNotFound;
        }

        var area = RouteDayPlan.Clip(command.AreaName);
        var notes = Clip(command.Notes, 500);
        var nothingToKeep = area is null && notes is null && command.FuelLitres is null && command.AccountSales is null;

        var entry = await db.VanTruckDayEntries
            .AsTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.RouteId == route.Id && candidate.TradingDate == day,
                cancellationToken);

        if (nothingToKeep)
        {
            if (entry is not null)
            {
                db.VanTruckDayEntries.Remove(entry);
                await db.SaveChangesAsync(cancellationToken);
                await LogAsync(route.Code, day, "cleared");
            }

            return new VanTruckDayEntryDto(route.Id, day, false, null, null, null, null, null, null);
        }

        var actor = command.ActingUserId is { } actorId
            ? await db.Users
                .AsNoTracking()
                .Where(user => user.Id == actorId)
                .Select(user => user.Username)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        if (entry is null)
        {
            entry = new VanTruckDayEntryEntity { RouteId = route.Id, TradingDate = day };
            db.VanTruckDayEntries.Add(entry);
        }
        else
        {
            entry.UpdatedAt = DateTime.UtcNow;
        }

        entry.AreaName = area;
        entry.FuelLitres = command.FuelLitres;
        entry.AccountSales = command.AccountSales;
        entry.Notes = notes;
        entry.UpdatedByUserId = command.ActingUserId;
        entry.UpdatedByName = actor;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Truck day entry saved for route {RouteCode} on {TradingDate:yyyy-MM-dd}: fuel {FuelLitres}, account sales {AccountSales}",
            route.Code, day, entry.FuelLitres, entry.AccountSales);

        await LogAsync(
            route.Code,
            day,
            $"fuel {Show(entry.FuelLitres, "L")}, account sales {Show(entry.AccountSales, "")}, area {entry.AreaName ?? "not given"}");

        return new VanTruckDayEntryDto(
            route.Id,
            day,
            true,
            entry.AreaName,
            entry.FuelLitres,
            entry.AccountSales,
            entry.Notes,
            entry.UpdatedByName,
            entry.UpdatedAt ?? entry.CreatedAt);
    }

    private async Task LogAsync(string routeCode, DateTime day, string what)
    {
        try
        {
            await auditService.LogAsync(
                "VanTruckDayEntrySaved",
                "VanTruckDayEntry",
                $"{routeCode}:{day:yyyy-MM-dd}",
                $"Route {routeCode} on {day:dd MMM yyyy}: {what}.",
                true);
        }
        catch
        {
        }
    }

    private static string Show(decimal? value, string unit) =>
        value is { } amount ? $"{amount:0.##}{unit}" : "not given";

    private static string? Clip(string? value, int maximumLength)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
