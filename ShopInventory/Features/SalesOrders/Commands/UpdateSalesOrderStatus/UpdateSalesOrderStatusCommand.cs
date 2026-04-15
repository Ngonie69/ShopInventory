using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.SalesOrders.Commands.UpdateSalesOrderStatus;

public sealed record UpdateSalesOrderStatusCommand(
    int Id,
    SalesOrderStatus Status,
    Guid UserId,
    string? Comments
) : IRequest<ErrorOr<SalesOrderDto>>;
