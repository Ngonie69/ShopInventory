using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesReports.Queries.GetRoutes;

public sealed class GetRoutesHandler(
    ApplicationDbContext db
) : IRequestHandler<GetRoutesQuery, ErrorOr<List<RouteDto>>>
{
    public async Task<ErrorOr<List<RouteDto>>> Handle(
        GetRoutesQuery query,
        CancellationToken cancellationToken)
    {
        var queryable = db.Routes.AsNoTracking();

        if (!query.IncludeInactive)
        {
            queryable = queryable.Where(route => route.IsActive);
        }

        return await queryable
            .OrderBy(route => route.Territory)
            .ThenBy(route => route.Name)
            .Select(route => new RouteDto(
                route.Id,
                route.Code,
                route.Name,
                route.Territory,
                route.TruckRegNo,
                route.IsActive,
                // How many vans run this route. A route with none is either new or forgotten, and the
                // management page should be able to show which without a second request.
                db.Users.Count(user => user.RouteId == route.Id)))
            .ToListAsync(cancellationToken);
    }
}
