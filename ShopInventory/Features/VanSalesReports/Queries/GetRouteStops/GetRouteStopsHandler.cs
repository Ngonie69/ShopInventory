using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;

public sealed class GetRouteStopsHandler(
    ApplicationDbContext db
) : IRequestHandler<GetRouteStopsQuery, ErrorOr<List<RouteStopDto>>>
{
    public async Task<ErrorOr<List<RouteStopDto>>> Handle(
        GetRouteStopsQuery query,
        CancellationToken cancellationToken)
    {
        var queryable = db.RouteStops
            .AsNoTracking()
            .Include(stop => stop.Route)
            .AsQueryable();

        if (query.RouteId is { } routeId)
        {
            queryable = queryable.Where(stop => stop.RouteId == routeId);
        }

        if (!query.IncludeInactive)
        {
            queryable = queryable.Where(stop => stop.IsActive);
        }

        return await queryable
            // Week before day, because the two never both apply to one route: an upcountry route
            // orders by its cycle week and a town truck by its weekday, and putting the week first
            // keeps each route's own stops in the order its schedule prints them.
            .OrderBy(stop => stop.Route!.Territory)
            .ThenBy(stop => stop.Route!.Name)
            .ThenBy(stop => stop.WeekNumber)
            .ThenBy(stop => stop.DayOfWeek)
            .ThenBy(stop => stop.AlternateSet)
            .ThenBy(stop => stop.Sequence)
            .ThenBy(stop => stop.Id)
            .Select(stop => new RouteStopDto(
                stop.Id,
                stop.RouteId,
                stop.Route!.Code,
                stop.Route!.Name,
                stop.Name,
                stop.DayOfWeek,
                stop.WeekNumber,
                stop.AlternateSet,
                stop.Sequence,
                stop.IsActive))
            .ToListAsync(cancellationToken);
    }
}
