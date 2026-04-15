using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SalesOrders.Commands.UpdateSalesOrder;

public sealed record UpdateSalesOrderCommand(
    int Id,
    CreateSalesOrderRequest Request
) : IRequest<ErrorOr<SalesOrderDto>>;
