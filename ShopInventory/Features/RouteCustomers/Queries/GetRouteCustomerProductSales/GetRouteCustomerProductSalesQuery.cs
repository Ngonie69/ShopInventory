using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductSales;

/// <summary>
/// What each route customer bought of each item over a window, one row per customer, item and currency.
/// Across every partner in the scope unless one is named.
/// </summary>
public sealed record GetRouteCustomerProductSalesQuery(
    string? AssignedBusinessPartnerCode,
    DateTime? From,
    DateTime? To,
    RouteCustomerScope Scope = RouteCustomerScope.Route
) : IRequest<ErrorOr<RouteCustomerProductSalesDto>>;
