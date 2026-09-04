using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;

/// <summary>
/// The areas the routes work — the published schedule. <c>RouteId</c> narrows it to one route.
/// </summary>
/// <remarks>
/// <c>IncludeInactive</c> brings back stops that have been dropped from the plan. The routes page
/// needs them to show what a round used to cover; a load list must not have them.
/// </remarks>
public sealed record GetRouteStopsQuery(int? RouteId = null, bool IncludeInactive = false)
    : IRequest<ErrorOr<List<RouteStopDto>>>;

/// <summary>
/// One planned stop. The route's code and name ride along because every caller that shows a stop
/// shows which round it belongs to, and asking for the routes separately to label them is a second
/// request for data the first one already joined.
/// </summary>
public sealed record RouteStopDto(
    int Id,
    int RouteId,
    string RouteCode,
    string RouteName,
    string Name,
    DayOfWeek? DayOfWeek,
    int? WeekNumber,
    int AlternateSet,
    int Sequence,
    bool IsActive
);
