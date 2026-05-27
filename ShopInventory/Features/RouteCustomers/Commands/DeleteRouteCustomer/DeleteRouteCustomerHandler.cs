using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.RouteCustomers.Commands.DeleteRouteCustomer;

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

        context.RouteCustomers.Remove(routeCustomer);
        await context.SaveChangesAsync(cancellationToken);

        return Result.Deleted;
    }
}