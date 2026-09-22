using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.DeleteRoute;

public sealed class DeleteRouteHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<DeleteRouteHandler> logger
) : IRequestHandler<DeleteRouteCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteRouteCommand command,
        CancellationToken cancellationToken)
    {
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken);

        if (route is null)
        {
            return Error.NotFound("Routes.NotFound", "That route no longer exists.");
        }

        if (route.DeletedAt is not null)
        {
            // Already gone. A second click on a slow page got what it asked for.
            return Result.Deleted;
        }

        var assigned = await db.Users.CountAsync(user => user.RouteId == route.Id, cancellationToken);

        if (assigned > 0)
        {
            return Error.Conflict(
                "Routes.StillAssigned",
                assigned == 1
                    ? "1 van is still on this route. Move it to another route before deleting the route."
                    : $"{assigned} vans are still on this route. Move them to another route before deleting it.");
        }

        var now = DateTime.UtcNow;

        route.DeletedAt = now;
        route.IsActive = false;
        route.UpdatedAt = now;

        // Its stops go with it, deactivated rather than removed so the seeder still finds their keys
        // and does not place them again.
        var stops = await db.RouteStops
            .Where(stop => stop.RouteId == route.Id && stop.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var stop in stops)
        {
            stop.IsActive = false;
            stop.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Route {Code} ({Name}) deleted with {StopCount} stop(s)",
            route.Code, route.Name, stops.Count);

        try
        {
            await auditService.LogAsync(
                "RouteDeleted",
                "Route",
                route.Id.ToString(),
                $"{route.Code} — {route.Name}",
                true);
        }
        catch
        {
        }

        return Result.Deleted;
    }
}
