using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductMix;

/// <summary>
/// What route customers actually buy, across a route or a single customer.
///
/// <c>Top</c> caps how many items come back, most-bought first; zero or less returns all of them.
/// </summary>
public sealed record GetRouteCustomerProductMixQuery(
    string? AssignedBusinessPartnerCode,
    int? RouteCustomerId,
    DateTime? From,
    DateTime? To,
    int Top
) : IRequest<ErrorOr<RouteCustomerProductMixDto>>;
