using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomers;

/// <summary>
/// The route customers — the van routes' shops — unless <paramref name="Scope"/> asks otherwise. A
/// vending vendor is not one of these: see <see cref="RouteCustomerScope"/>.
/// </summary>
public sealed record GetRouteCustomersQuery(
    string? AssignedBusinessPartnerCode = null,
    bool ActiveOnly = true,
    RouteCustomerScope Scope = RouteCustomerScope.Route) : IRequest<ErrorOr<List<RouteCustomerDto>>>;
