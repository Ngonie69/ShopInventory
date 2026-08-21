using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomerHistory;

/// <summary>
/// The handset's half of the customer drill-down: resolve which shop on this rep's route the code
/// names, then read the same detail the office reads.
///
/// Same query as the web report deliberately. What a rep is told a shop has bought, standing in that
/// shop, has to be what the office would say about it — two readings of one customer's trading that
/// can disagree is worse than one that is merely inconvenient to reach.
/// </summary>
public sealed class GetVanSalesCustomerHistoryHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<GetVanSalesCustomerHistoryQuery, ErrorOr<RouteCustomerSalesDetailDto>>
{
    public async Task<ErrorOr<RouteCustomerSalesDetailDto>> Handle(
        GetVanSalesCustomerHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

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

        // The management question rather than the trading one: this is a read, and a card left open
        // when a removal lands should still show what the shop bought rather than turning into a
        // "not found" the rep cannot explain.
        var routeCustomer = await VanSalesRouteCustomerScope.FindOnRouteAsync(
            db,
            VanSalesRouteCustomerScope.GetAssignedBusinessPartnerCode(user),
            query.Code,
            cancellationToken);

        if (routeCustomer is null)
        {
            return Errors.RouteCustomers.CodeNotFoundOnRoute(query.Code ?? string.Empty);
        }

        return await mediator.Send(
            new GetRouteCustomerSalesQuery(routeCustomer.Id, query.From, query.To),
            cancellationToken);
    }
}
