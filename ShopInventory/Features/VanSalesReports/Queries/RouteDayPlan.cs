using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>
/// The area a route's published plan sends it to on a given day, as one line of text.
/// </summary>
/// <remarks>
/// <para>
/// Only the part of the plan that a date settles. A town truck works named areas on a weekday, every
/// week, so its Thursday is known: "Glenorah". Those are the stops with a weekday and no cycle week,
/// in the standard set.
/// </para>
/// <para>
/// Two things a date does not settle, and this does not guess at. An upcountry trip runs a cycle of
/// weeks with no weekday and no anchor saying which week a date falls in. And a day with a published
/// alternative has two plans; the standard one is named here, and which ran is for the rep or the
/// office to say. Both leave the day to <see cref="VanTruckDayEntryEntity.AreaName"/>.
/// </para>
/// </remarks>
public static class RouteDayPlan
{
    /// <summary>What a stop must say for the plan to place it on a day.</summary>
    public readonly record struct Stop(int RouteId, string Name, DayOfWeek? DayOfWeek, int? WeekNumber, int AlternateSet, int Sequence);

    /// <summary>The longest an area line may be; the column it is stored in.</summary>
    public const int MaximumLength = 200;

    /// <summary>The standard weekday stops, in schedule order, or null when the plan names none for that day.</summary>
    public static string? AreaFor(IEnumerable<Stop> stops, DayOfWeek day)
    {
        var names = stops
            .Where(stop => stop.DayOfWeek == day && stop.WeekNumber is null && stop.AlternateSet == 0)
            .OrderBy(stop => stop.Sequence)
            .ThenBy(stop => stop.Name, StringComparer.OrdinalIgnoreCase)
            .Select(stop => stop.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? null : Clip(string.Join(", ", names));
    }

    /// <summary>Every active stop of these routes, for placing many days at once.</summary>
    public static async Task<ILookup<int, Stop>> LoadAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<int> routeIds,
        CancellationToken cancellationToken)
    {
        if (routeIds.Count == 0)
        {
            return Array.Empty<Stop>().ToLookup(stop => stop.RouteId);
        }

        var stops = await db.RouteStops
            .AsNoTracking()
            .Where(stop => stop.IsActive && routeIds.Contains(stop.RouteId))
            .Select(stop => new Stop(stop.RouteId, stop.Name, stop.DayOfWeek, stop.WeekNumber, stop.AlternateSet, stop.Sequence))
            .ToListAsync(cancellationToken);

        return stops.ToLookup(stop => stop.RouteId);
    }

    /// <summary>An area line trimmed to what the column holds, or null when there is nothing in it.</summary>
    public static string? Clip(string? area)
    {
        var trimmed = area?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaximumLength ? trimmed : trimmed[..MaximumLength];
    }
}
