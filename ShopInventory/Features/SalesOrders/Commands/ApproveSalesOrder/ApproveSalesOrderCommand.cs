using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SalesOrders.Commands.ApproveSalesOrder;

public sealed record ApproveSalesOrderCommand(int Id, Guid UserId) : IRequest<ErrorOr<SalesOrderDto>>;
