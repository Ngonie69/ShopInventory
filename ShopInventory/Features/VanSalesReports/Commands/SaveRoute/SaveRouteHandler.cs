using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetRoutes;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.SaveRoute;

public sealed class SaveRouteHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<SaveRouteHandler> logger
) : IRequestHandler<SaveRouteCommand, ErrorOr<RouteDto>>
{
    public async Task<ErrorOr<RouteDto>> Handle(
        SaveRouteCommand command,
        CancellationToken cancellationToken)
    {
        var code = command.Code?.Trim();
        var name = command.Name?.Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            return Error.Validation("Routes.CodeRequired", "A route needs a code.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("Routes.NameRequired", "A route needs a name.");
        }

        // A round with one limit set and not the other is almost always a half-finished edit, and
        // accepting it would judge the load against an open-ended range that can never be breached
        // on the missing side — a cold-chain check that quietly only checks one direction.
        if (command.TemperatureMinC.HasValue != command.TemperatureMaxC.HasValue)
        {
            return Error.Validation(
                "Routes.TemperatureRangeIncomplete",
                "Set both temperature limits, or neither. A route with no limits is not judged on temperature.");
        }

        if (command.TemperatureMinC is { } minimum
            && command.TemperatureMaxC is { } maximum
            && minimum > maximum)
        {
            return Error.Validation(
                "Routes.TemperatureRangeInverted",
                $"The lower temperature limit ({minimum:0.#} °C) is above the upper one ({maximum:0.#} °C).");
        }

        if (command.TemperatureProbeChannel is { } channel && channel is < 1 or > 4)
        {
            return Error.Validation(
                "Routes.TemperatureProbeUnknown",
                "The tracker reports four temperature probes, so the channel has to be 1, 2, 3 or 4.");
        }

        var clash = await db.Routes
            .AsNoTracking()
            .AnyAsync(
                route => route.Code == code
                    && route.DeletedAt == null
                    && (command.Id == null || route.Id != command.Id.Value),
                cancellationToken);

        if (clash)
        {
            return Error.Conflict("Routes.DuplicateCode", $"Another route already uses the code {code}.");
        }

        RouteEntity route;

        if (command.Id is { } id)
        {
            var existing = await db.Routes.FirstOrDefaultAsync(
                r => r.Id == id && r.DeletedAt == null,
                cancellationToken);

            if (existing is null)
            {
                return Error.NotFound("Routes.NotFound", "That route no longer exists.");
            }

            // Deactivating a route that vans are still assigned to would leave them with no territory
            // and no truck the next morning, and the failure would show up as a blank compliance
            // sheet rather than as an error. Ask for the reassignment first.
            if (existing.IsActive && !command.IsActive)
            {
                var assigned = await db.Users.CountAsync(user => user.RouteId == id, cancellationToken);

                if (assigned > 0)
                {
                    return Error.Conflict(
                        "Routes.StillAssigned",
                        $"{assigned} {(assigned == 1 ? "van is" : "vans are")} still on this route. " +
                        "Move them to another route before retiring it.");
                }
            }

            existing.Code = code;
            existing.Name = name;
            existing.Territory = NullIfBlank(command.Territory);
            existing.TruckRegNo = NullIfBlank(command.TruckRegNo);
            existing.TemperatureMinC = command.TemperatureMinC;
            existing.TemperatureMaxC = command.TemperatureMaxC;
            existing.TemperatureProbeChannel = command.TemperatureProbeChannel;
            existing.IsActive = command.IsActive;
            existing.UpdatedAt = DateTime.UtcNow;

            route = existing;
        }
        else
        {
            route = new RouteEntity
            {
                Code = code,
                Name = name,
                Territory = NullIfBlank(command.Territory),
                TruckRegNo = NullIfBlank(command.TruckRegNo),
                TemperatureMinC = command.TemperatureMinC,
                TemperatureMaxC = command.TemperatureMaxC,
                TemperatureProbeChannel = command.TemperatureProbeChannel,
                IsActive = command.IsActive,
                CreatedByUserId = command.ActingUserId
            };

            db.Routes.Add(route);
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Route {Code} ({Name}) saved in territory {Territory}",
            route.Code, route.Name, route.Territory ?? "(none)");

        try
        {
            await auditService.LogAsync(
                command.Id is null ? "RouteCreated" : "RouteUpdated",
                "Route",
                route.Id.ToString(),
                $"{route.Code} — {route.Name}" +
                (string.IsNullOrWhiteSpace(route.Territory) ? "" : $", territory {route.Territory}") +
                (string.IsNullOrWhiteSpace(route.TruckRegNo) ? "" : $", truck {route.TruckRegNo}"),
                true);
        }
        catch
        {
        }

        var assignedCount = await db.Users.CountAsync(user => user.RouteId == route.Id, cancellationToken);

        return new RouteDto(
            route.Id,
            route.Code,
            route.Name,
            route.Territory,
            route.TruckRegNo,
            route.TemperatureMinC,
            route.TemperatureMaxC,
            route.TemperatureProbeChannel,
            route.IsActive,
            assignedCount);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
