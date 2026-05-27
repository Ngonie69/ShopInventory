using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomers;

public sealed record GetRouteCustomersQuery(
    string? AssignedBusinessPartnerCode = null,
    bool ActiveOnly = true) : IRequest<ErrorOr<List<RouteCustomerDto>>>;