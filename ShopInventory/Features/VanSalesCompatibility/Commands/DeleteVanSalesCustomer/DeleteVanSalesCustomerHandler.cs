using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Commands.DeleteRouteCustomer;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.DeleteVanSalesCustomer;

/// <summary>
/// Lets a rep take a shop they no longer service off their handset.
///
/// This resolves which row the code names on the caller's own route and then hands off to
/// <see cref="DeleteRouteCustomerCommand"/>, which deactivates rather than deletes. Deactivating is
/// the whole point: the van stops seeing the shop on its next sync, and the office keeps every sale
/// ever made to it, still attributed, for the route's trend reporting.
///
/// Delegating rather than setting <c>IsActive</c> here matters more than it looks. There would
/// otherwise be two places that remove a customer, reachable by two audiences, and the one used by
/// handsets is the one nobody watches — so it is the one that would quietly drift into a hard delete
/// and take a route's history with it.
/// </summary>
public sealed class DeleteVanSalesCustomerHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<DeleteVanSalesCustomerCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteVanSalesCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null)
        {
            return Errors.RouteCustomers.UserNotFound;
        }

        if (!user.IsActive)
        {
            return Errors.RouteCustomers.UserInactive;
        }

        // Only a route that keeps its own customers has one to remove. An account served by head
        // office is refused rather than quietly doing nothing, because doing nothing looks to the
        // rep exactly like it worked until the shop is still there tomorrow.
        if (!VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            return Errors.RouteCustomers.RouteCustomersNotManagedHere;
        }

        var routeCustomer = await VanSalesRouteCustomerScope.FindOnRouteAsync(
            db,
            VanSalesRouteCustomerScope.GetAssignedBusinessPartnerCode(user),
            command.Code,
            cancellationToken);

        if (routeCustomer is null)
        {
            return Errors.RouteCustomers.CodeNotFoundOnRoute(command.Code ?? string.Empty);
        }

        // Already gone. Said as success, not as a failure: this arrives from a queue that replays a
        // removal recorded without signal, so the second delivery of one is ordinary rather than
        // wrong, and reporting it as an error would leave the handset retrying it for good.
        if (!routeCustomer.IsActive)
        {
            return Result.Deleted;
        }

        return await mediator.Send(new DeleteRouteCustomerCommand(routeCustomer.Id), cancellationToken);
    }
}
