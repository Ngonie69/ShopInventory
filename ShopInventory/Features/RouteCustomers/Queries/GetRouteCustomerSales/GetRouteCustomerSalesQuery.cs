using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;

/// <summary>
/// Everything one route customer has bought inside a window: the sales themselves, the orders they have
/// outstanding, and what they buy most.
/// </summary>
public sealed record GetRouteCustomerSalesQuery(
    int RouteCustomerId,
    DateTime? From,
    DateTime? To
) : IRequest<ErrorOr<RouteCustomerSalesDetailDto>>;
