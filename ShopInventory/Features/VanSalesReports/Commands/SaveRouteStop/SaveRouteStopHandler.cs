using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.SaveRouteStop;

public sealed class SaveRouteStopHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<SaveRouteStopHandler> logger
) : IRequestHandler<SaveRouteStopCommand, ErrorOr<RouteStopDto>>
{
    public async Task<ErrorOr<RouteStopDto>> Handle(
        SaveRouteStopCommand command,
        CancellationToken cancellationToken)
    {
        var name = command.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("RouteStops.NameRequired", "A stop needs an area name.");
        }

        if (name.Length > 100)
        {
            return Error.Validation("RouteStops.NameTooLong", "An area name may be at most 100 characters.");
        }

        if (command.WeekNumber is { } week && week < 1)
        {
            return Error.Validation(
                "RouteStops.WeekOutOfRange",
                "A cycle week is numbered from 1. Leave it blank for a stop worked every week.");
        }

        if (command.AlternateSet < 0)
        {
            return Error.Validation(
                "RouteStops.AlternateSetOutOfRange",
                "The standard plan is set 0; an alternative is 1 or above.");
        }

        var route = await db.Routes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == command.RouteId, cancellationToken);

        if (route is null)
        {
            return Error.NotFound("RouteStops.RouteNotFound", "That route no longer exists.");
        }

        // The same area twice on the same day is a data-entry slip, not a round that calls twice.
        // Matched in memory: the key spans two nullable columns, and a NULL-to-NULL comparison in SQL
        // is never true, so the obvious Where clause would let every upcountry duplicate through.
        var siblings = await db.RouteStops
            .AsNoTracking()
            .Where(stop => stop.RouteId == command.RouteId)
            .ToListAsync(cancellationToken);

        var clash = siblings.FirstOrDefault(stop =>
            stop.Id != command.Id &&
            stop.DayOfWeek == command.DayOfWeek &&
            stop.WeekNumber == command.WeekNumber &&
            stop.AlternateSet == command.AlternateSet &&
            string.Equals(stop.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

        // A dropped stop being added back is that stop returning to the plan, so the row is revived
        // rather than duplicated. Refusing instead would be a dead end: the panel shows only active
        // stops, so the reader would be told to edit one they cannot see. The seeder deliberately
        // does not do this — a deploy must not undo a decision — but a person typing the name in has
        // made that decision.
        if (clash is { IsActive: false } && command.Id is null)
        {
            command = command with { Id = clash.Id };
        }
        else if (clash is not null)
        {
            // Named as the schedule spells it rather than as it was just typed. The match is
            // case-insensitive, so echoing the input would answer "already works waterfalls" about a
            // stop the page shows as "Waterfalls" — which reads as a different stop, and sends the
            // reader looking for one that is not there.
            return Error.Conflict(
                "RouteStops.Duplicate",
                $"{route.Name} already works {clash.Name} {WhenPhrase(clash)}.");
        }

        RouteStopEntity entity;

        if (command.Id is { } id)
        {
            var existing = await db.RouteStops.FirstOrDefaultAsync(stop => stop.Id == id, cancellationToken);

            if (existing is null)
            {
                return Error.NotFound("RouteStops.NotFound", "That stop no longer exists.");
            }

            existing.RouteId = command.RouteId;
            existing.Name = name;
            existing.DayOfWeek = command.DayOfWeek;
            existing.WeekNumber = command.WeekNumber;
            existing.AlternateSet = command.AlternateSet;
            existing.Sequence = command.Sequence ?? existing.Sequence;
            existing.IsActive = command.IsActive;
            existing.UpdatedAt = DateTime.UtcNow;

            entity = existing;
        }
        else
        {
            entity = new RouteStopEntity
            {
                RouteId = command.RouteId,
                Name = name,
                DayOfWeek = command.DayOfWeek,
                WeekNumber = command.WeekNumber,
                AlternateSet = command.AlternateSet,
                // Appended to its own day or week when the caller does not say where it goes, so a
                // stop added from the page lands at the end of the list it was added to rather than
                // silently sharing position 0 with the first one.
                Sequence = command.Sequence ?? NextSequence(siblings, command),
                IsActive = command.IsActive
            };

            db.RouteStops.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Route stop {Name} saved on {RouteCode} ({When})",
            entity.Name, route.Code, Describe(entity));

        try
        {
            await auditService.LogAsync(
                command.Id is null ? "RouteStopCreated" : "RouteStopUpdated",
                "RouteStop",
                entity.Id.ToString(),
                $"{route.Code} — {entity.Name} ({Describe(entity)})",
                true);
        }
        catch
        {
        }

        return new RouteStopDto(
            entity.Id,
            entity.RouteId,
            route.Code,
            route.Name,
            entity.Name,
            entity.DayOfWeek,
            entity.WeekNumber,
            entity.AlternateSet,
            entity.Sequence,
            entity.IsActive);
    }

    private static int NextSequence(List<RouteStopEntity> siblings, SaveRouteStopCommand command)
    {
        var inSameSet = siblings
            .Where(stop =>
                stop.DayOfWeek == command.DayOfWeek &&
                stop.WeekNumber == command.WeekNumber &&
                stop.AlternateSet == command.AlternateSet)
            .ToList();

        return inSameSet.Count == 0 ? 1 : inSameSet.Max(stop => stop.Sequence) + 1;
    }

    /// <summary>
    /// When the stop is worked, as a phrase that can follow a verb: "on Monday", "in week 1".
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Describe"/>, which labels a stop for the log and the audit trail and
    /// is right to be terse there. A message shown to a person needs the preposition its particular
    /// shape takes, and there is no single one that fits all four — "on week 1" and "on no set day"
    /// are what one preposition for everything produces.
    /// </remarks>
    private static string WhenPhrase(RouteStopEntity stop)
    {
        var when = stop switch
        {
            { DayOfWeek: { } day, WeekNumber: { } week } => $"on {day} in week {week}",
            { DayOfWeek: { } day } => $"on {day}",
            { WeekNumber: { } week } => $"in week {week}",
            _ => "with no set day"
        };

        return stop.AlternateSet == 0 ? when : $"{when}, on the alternative plan";
    }

    private static string Describe(RouteStopEntity stop)
    {
        var when = stop switch
        {
            { DayOfWeek: { } day, WeekNumber: { } week } => $"{day}, week {week}",
            { DayOfWeek: { } day } => day.ToString(),
            { WeekNumber: { } week } => $"week {week}",
            _ => "no set day"
        };

        return stop.AlternateSet == 0 ? when : $"{when}, alternative {stop.AlternateSet}";
    }
}
