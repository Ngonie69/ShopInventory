using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.DeleteRouteStop;

public sealed class DeleteRouteStopHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<DeleteRouteStopHandler> logger
) : IRequestHandler<DeleteRouteStopCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteRouteStopCommand command,
        CancellationToken cancellationToken)
    {
        var stop = await db.RouteStops
            .Include(s => s.Route)
            .FirstOrDefaultAsync(s => s.Id == command.Id, cancellationToken);

        if (stop is null)
        {
            return Error.NotFound("RouteStops.NotFound", "That stop no longer exists.");
        }

        if (!stop.IsActive)
        {
            // Already dropped. Answered as success rather than as a conflict because the caller asked
            // for a state, not for a transition, and a second click on a slow page must not report a
            // failure for having got what it wanted.
            return Result.Deleted;
        }

        stop.IsActive = false;
        stop.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Route stop {Name} dropped from {RouteCode}",
            stop.Name, stop.Route?.Code ?? stop.RouteId.ToString());

        try
        {
            await auditService.LogAsync(
                "RouteStopRemoved",
                "RouteStop",
                stop.Id.ToString(),
                $"{stop.Route?.Code ?? stop.RouteId.ToString()} — {stop.Name}",
                true);
        }
        catch
        {
        }

        return Result.Deleted;
    }
}
