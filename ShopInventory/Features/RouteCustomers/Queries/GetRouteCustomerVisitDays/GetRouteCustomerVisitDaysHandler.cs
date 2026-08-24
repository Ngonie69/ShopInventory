using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerVisitDays;

/// <summary>
/// Reads calling patterns, including for shops that have none.
/// </summary>
/// <remarks>
/// Driven from the customers rather than from the visit-day rows, so a shop with nothing scheduled
/// comes back with an empty list instead of not coming back at all. Those are precisely the rows an
/// operator is looking for when filling the schedule in.
/// </remarks>
public sealed class GetRouteCustomerVisitDaysHandler(ApplicationDbContext context)
    : IRequestHandler<GetRouteCustomerVisitDaysQuery, ErrorOr<List<RouteCustomerVisitDaysResult>>>
{
    public async Task<ErrorOr<List<RouteCustomerVisitDaysResult>>> Handle(
        GetRouteCustomerVisitDaysQuery query,
        CancellationToken cancellationToken)
    {
        var customers = context.RouteCustomers.AsNoTracking().Where(c => c.IsActive);

        if (query.RouteCustomerId is { } routeCustomerId)
        {
            customers = customers.Where(c => c.Id == routeCustomerId);
        }

        if (!string.IsNullOrWhiteSpace(query.AssignedBusinessPartnerCode))
        {
            customers = customers.Where(c =>
                c.AssignedBusinessPartnerCode == query.AssignedBusinessPartnerCode);
        }

        var results = await customers
            .OrderBy(c => c.Name)
            .Select(c => new RouteCustomerVisitDaysResult(
                c.Id,
                c.Code,
                c.Name,
                context.RouteCustomerVisitDays
                    .Where(d => d.RouteCustomerId == c.Id)
                    .OrderBy(d => d.DayOfWeek)
                    .Select(d => d.DayOfWeek)
                    .ToList()))
            .ToListAsync(cancellationToken);

        return results;
    }
}
