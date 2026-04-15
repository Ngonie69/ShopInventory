using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SalesOrders.Commands.CreateSalesOrder;

public sealed record CreateSalesOrderCommand(
    CreateSalesOrderRequest Request,
    Guid UserId
) : IRequest<ErrorOr<SalesOrderDto>>;
