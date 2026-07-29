using ErrorOr;
using MediatR;

namespace ShopInventory.Features.RouteCustomers.Commands.DeleteRouteCustomer;

public sealed record DeleteRouteCustomerCommand(int Id) : IRequest<ErrorOr<Deleted>>;