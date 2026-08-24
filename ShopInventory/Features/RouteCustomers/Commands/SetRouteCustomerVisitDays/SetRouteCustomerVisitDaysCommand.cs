using ErrorOr;
using MediatR;

namespace ShopInventory.Features.RouteCustomers.Commands.SetRouteCustomerVisitDays;

/// <summary>
/// Set the weekdays the van calls on a shop, replacing whatever was there.
/// </summary>
/// <remarks>
/// Replace rather than add or remove one at a time: the operator screen shows seven checkboxes and
/// submits the answer, and a set of add/remove calls would let a dropped request leave the schedule
/// half-applied with nothing to say so.
/// </remarks>
public sealed record SetRouteCustomerVisitDaysCommand(
    int RouteCustomerId,
    IReadOnlyList<DayOfWeek> VisitDays
) : IRequest<ErrorOr<RouteCustomerVisitDaysResult>>;
