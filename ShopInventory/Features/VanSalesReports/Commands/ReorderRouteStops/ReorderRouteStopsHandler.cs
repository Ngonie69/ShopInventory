using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.ReorderRouteStops;

public sealed class ReorderRouteStopsHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<ReorderRouteStopsHandler> logger
) : IRequestHandler<ReorderRouteStopsCommand, ErrorOr<List<RouteStopDto>>>
{
    public async Task<ErrorOr<List<RouteStopDto>>> Handle(
        ReorderRouteStopsCommand command,
        CancellationToken cancellationToken)
    {
        if (command.StopIds is null || command.StopIds.Count == 0)
        {
            return Error.Validation("RouteStops.OrderEmpty", "Say which stops, and in what order.");
        }

        if (command.StopIds.Distinct().Count() != command.StopIds.Count)
        {
            return Error.Validation(
                "RouteStops.OrderRepeatsAStop",
                "That order names the same stop twice.");
        }

        var route = await db.Routes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == command.RouteId, cancellationToken);

        if (route is null)
        {
            return Error.NotFound("RouteStops.RouteNotFound", "That route no longer exists.");
        }

        var stops = await db.RouteStops
            .Where(stop => stop.RouteId == command.RouteId)
            .ToListAsync(cancellationToken);

        // Filtered in memory. The heading spans two nullable columns and a NULL-to-NULL comparison in
        // SQL is never true, so an upcountry heading matched in a Where clause would come back empty
        // and every reorder of one would be refused as "no longer there".
        var group = stops
            .Where(stop =>
                stop.IsActive &&
                stop.DayOfWeek == command.DayOfWeek &&
                stop.WeekNumber == command.WeekNumber &&
                stop.AlternateSet == command.AlternateSet)
            .ToList();

        if (group.Count == 0)
        {
            return Error.NotFound(
                "RouteStops.GroupNotFound",
                "That part of the plan no longer has any stops.");
        }

        // Set equality, not a subset test. An order that omits a stop would leave it holding whatever
        // position it had — usually in the middle of the new numbering — and an order naming a stop
        // from another heading would move it out of its own. Both come from a page that has gone
        // stale, and the answer to a stale page is to say so, not to apply half of it.
        var held = group.Select(stop => stop.Id).ToHashSet();

        if (!held.SetEquals(command.StopIds))
        {
            return Error.Conflict(
                "RouteStops.OrderStale",
                "The plan has changed since this page was loaded. Reload and try again.");
        }

        var byId = group.ToDictionary(stop => stop.Id);
        var now = DateTime.UtcNow;
        var position = 1;
        var moved = 0;

        foreach (var id in command.StopIds)
        {
            var stop = byId[id];

            if (stop.Sequence != position)
            {
                stop.Sequence = position;
                stop.UpdatedAt = now;
                moved++;
            }

            position++;
        }

        if (moved > 0)
        {
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Reordered {Count} stop(s) on {RouteCode} ({When})",
                moved, route.Code, Describe(command));

            try
            {
                await auditService.LogAsync(
                    "RouteStopsReordered",
                    "Route",
                    route.Id.ToString(),
                    $"{route.Code} — {Describe(command)}: " +
                    string.Join(", ", command.StopIds.Select(id => byId[id].Name)),
                    true);
            }
            catch
            {
            }
        }

        return command.StopIds
            .Select(id => byId[id])
            .Select(stop => new RouteStopDto(
                stop.Id,
                stop.RouteId,
                route.Code,
                route.Name,
                stop.Name,
                stop.DayOfWeek,
                stop.WeekNumber,
                stop.AlternateSet,
                stop.Sequence,
                stop.IsActive))
            .ToList();
    }

    private static string Describe(ReorderRouteStopsCommand command)
    {
        var when = (command.DayOfWeek, command.WeekNumber) switch
        {
            ({ } day, { } week) => $"{day}, week {week}",
            ({ } day, null) => day.ToString(),
            (null, { } week) => $"week {week}",
            _ => "unscheduled"
        };

        return command.AlternateSet == 0 ? when : $"{when}, alternative {command.AlternateSet}";
    }
}
