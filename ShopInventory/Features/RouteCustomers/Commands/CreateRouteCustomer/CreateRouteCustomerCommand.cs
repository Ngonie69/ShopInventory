using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;

public sealed record CreateRouteCustomerCommand(
    CreateRouteCustomerRequest Request,
    Guid UserId) : IRequest<ErrorOr<RouteCustomerDto>>;