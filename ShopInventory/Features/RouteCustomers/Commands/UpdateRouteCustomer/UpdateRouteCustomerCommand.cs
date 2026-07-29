using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Commands.UpdateRouteCustomer;

public sealed record UpdateRouteCustomerCommand(
    int Id,
    UpdateRouteCustomerRequest Request) : IRequest<ErrorOr<RouteCustomerDto>>;