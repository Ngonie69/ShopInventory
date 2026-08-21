using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Commands.UpdateRouteCustomer;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.UpdateVanSalesCustomer;

/// <summary>
/// Lets a rep correct what the handset holds for a shop on their route.
///
/// The route, the code and the active flag are not taken from the request — they are read off the
/// row and handed back unchanged. That is what makes this safe to expose to a handset while
/// <see cref="UpdateRouteCustomerCommand"/>, which it delegates to, stays as open as an
/// administrator needs. A request that names a different route would otherwise move the shop to
/// another van; one that names a different code would rename the identity every sale and every
/// queued document already refers to; and one that sets the flag would perform the removal, which
/// has its own permission and is not this.
/// </summary>
public sealed class UpdateVanSalesCustomerHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IBusinessPartnerService businessPartnerService
) : IRequestHandler<UpdateVanSalesCustomerCommand, ErrorOr<VanSalesShopDto>>
{
    public async Task<ErrorOr<VanSalesShopDto>> Handle(
        UpdateVanSalesCustomerCommand command,
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

        if (!VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            return Errors.RouteCustomers.RouteCustomersNotManagedHere;
        }

        var assignedBusinessPartnerCode = VanSalesRouteCustomerScope.GetAssignedBusinessPartnerCode(user);

        // The trading question rather than the management one: a shop the rep has already removed is
        // not there to correct. Editing it would put a customer back in front of whoever reads the
        // record without putting it back on the route, which is neither of the two states anyone
        // means.
        var routeCustomer = await VanSalesRouteCustomerScope.FindAssignableAsync(
            db,
            assignedBusinessPartnerCode,
            command.Code,
            cancellationToken);

        if (routeCustomer is null)
        {
            return Errors.RouteCustomers.CodeNotFoundOnRoute(command.Code ?? string.Empty);
        }

        var result = await mediator.Send(
            new UpdateRouteCustomerCommand(
                routeCustomer.Id,
                new UpdateRouteCustomerRequest
                {
                    // Off the row, never off the request. See the summary above.
                    AssignedBusinessPartnerCode = routeCustomer.AssignedBusinessPartnerCode,
                    Code = routeCustomer.Code,
                    IsActive = routeCustomer.IsActive,

                    Name = command.Request.Name,
                    Phone = command.Request.Phone,
                    Email = command.Request.Email,
                    Address = command.Request.Address,
                    VatNumber = command.Request.VatNumber
                }),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        var updated = await VanSalesRouteCustomerScope.FindAssignableAsync(
            db,
            assignedBusinessPartnerCode,
            routeCustomer.Code,
            cancellationToken);

        if (updated is null)
        {
            return Errors.RouteCustomers.CodeNotFoundOnRoute(routeCustomer.Code);
        }

        var partner = string.IsNullOrWhiteSpace(assignedBusinessPartnerCode)
            ? null
            : await businessPartnerService.GetBusinessPartnerByCodeAsync(assignedBusinessPartnerCode, cancellationToken);

        return VanSalesCompatibilityMapper.MapShop(user, updated, partner);
    }
}
