using ErrorOr;
using MediatR;
using ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;

namespace ShopInventory.Features.VanSalesReports.Commands.SaveRouteStop;

/// <summary>
/// Adds an area to a route's plan, or edits one. <c>Id</c> null means add.
/// </summary>
/// <remarks>
/// One command for both, as <c>SaveRouteCommand</c> is, because the two differ in a single branch
/// and are validated identically.
/// </remarks>
public sealed record SaveRouteStopCommand(
    int? Id,
    int RouteId,
    string Name,
    DayOfWeek? DayOfWeek,
    int? WeekNumber,
    int AlternateSet,
    int? Sequence,
    bool IsActive,
    Guid? ActingUserId
) : IRequest<ErrorOr<RouteStopDto>>;
