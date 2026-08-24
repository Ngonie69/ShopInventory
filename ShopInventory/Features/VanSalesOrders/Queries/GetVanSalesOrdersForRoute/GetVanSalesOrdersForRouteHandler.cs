using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesOrdersForRoute;

/// <summary>
/// Builds a van's load list from the orders its customers have placed.
/// </summary>
/// <remarks>
/// Two views of the same orders, because the depot and the rep need different things from them. The
/// per-item totals are what gets loaded onto the truck; the orders themselves are what gets handed
/// over at each door.
/// <para>
/// Defaults to open orders only. A cancelled or already-delivered order on a load list is stock
/// loaded for nobody, and the whole point of the list is that it can be trusted at face value.
/// </para>
/// </remarks>
public sealed class GetVanSalesOrdersForRouteHandler(ApplicationDbContext context)
    : IRequestHandler<GetVanSalesOrdersForRouteQuery, ErrorOr<VanSalesRouteLoadResult>>
{
    public async Task<ErrorOr<VanSalesRouteLoadResult>> Handle(
        GetVanSalesOrdersForRouteQuery query,
        CancellationToken cancellationToken)
    {
        var orders = context.VanSalesOrders.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.AssignedBusinessPartnerCode))
        {
            orders = orders.Where(o => o.AssignedBusinessPartnerCode == query.AssignedBusinessPartnerCode);
        }

        if (!string.IsNullOrWhiteSpace(query.RouteCode))
        {
            orders = orders.Where(o => o.RouteCode == query.RouteCode);
        }

        if (query.VisitDate is { } visitDate)
        {
            var date = visitDate.Date;
            orders = orders.Where(o => o.RequestedVisitDate == date);
        }

        // Open by default. Everything else on a load list is stock loaded for nobody.
        orders = query.Status is { } status
            ? orders.Where(o => o.Status == status)
            : orders.Where(o => o.Status == VanSalesOrderStatus.Accepted);

        var results = await orders
            .OrderBy(o => o.RouteCustomerName)
            .ThenBy(o => o.Id)
            .Select(VanSalesOrderProjection.ToResult)
            .ToListAsync(cancellationToken);

        // Totalled in memory over an already-materialised page rather than as a second database
        // round trip, so the two halves of this answer cannot describe different sets of orders.
        var loadLines = results
            .SelectMany(order => order.Lines.Select(line => new { order.Id, Line = line }))
            .GroupBy(x => x.Line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VanSalesLoadLine(
                g.Key,
                g.Select(x => x.Line.ItemDescription).FirstOrDefault(d => d is not null),
                g.Select(x => x.Line.UnitOfMeasure).FirstOrDefault(u => u is not null),
                g.Sum(x => x.Line.QuantityOrdered),
                g.Select(x => x.Id).Distinct().Count()))
            .OrderBy(l => l.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VanSalesRouteLoadResult(
            query.VisitDate?.Date,
            query.RouteCode,
            results.Count,
            results.Sum(o => o.DocTotal),
            loadLines,
            results);
    }
}
