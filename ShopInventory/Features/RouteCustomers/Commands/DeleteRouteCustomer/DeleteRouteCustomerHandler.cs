using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.RouteCustomers.Commands.DeleteRouteCustomer;

/// <summary>
/// Deletes a route customer by deactivating it, not by removing the row.
///
/// The two audiences want opposite things. The van stops selling to a shop and wants it off the
/// handset; the office still wants last quarter's takings from that shop in the route's trend. A
/// hard delete served the first and quietly cost the second — the three sales tables null their
/// <c>RouteCustomerId</c> on delete, so the shop's history survived only as loose snapshot columns
/// and its drill-down 404'd.
///
/// Deactivating gives both. <see cref="Common.Mobile.VanSalesRouteCustomerScope"/> already filters
/// on <c>IsActive</c>, so the handset loses it on its next sync; every report reads the row
/// regardless, so the history stays whole and attributed.
/// </summary>
public sealed class DeleteRouteCustomerHandler(
    ApplicationDbContext context
) : IRequestHandler<DeleteRouteCustomerCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteRouteCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var routeCustomer = await context.RouteCustomers.FindAsync([command.Id], cancellationToken);
        if (routeCustomer is null)
        {
            return Errors.RouteCustomers.NotFound(command.Id);
        }

        // Already removed. Deleting twice is the same outcome as deleting once, and reporting it as
        // a failure would only tell the caller its own list was stale.
        if (!routeCustomer.IsActive)
        {
            return Result.Deleted;
        }

        routeCustomer.IsActive = false;
        routeCustomer.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        return Result.Deleted;
    }
}
