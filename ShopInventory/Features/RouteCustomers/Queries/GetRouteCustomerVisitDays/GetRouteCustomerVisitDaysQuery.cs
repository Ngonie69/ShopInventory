using ErrorOr;
using MediatR;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerVisitDays;

/// <summary>
/// The calling pattern for one shop, or for every shop on a van's route.
/// </summary>
public sealed record GetRouteCustomerVisitDaysQuery(
    int? RouteCustomerId,
    string? AssignedBusinessPartnerCode
) : IRequest<ErrorOr<List<RouteCustomerVisitDaysResult>>>;
