using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.PurchaseOrders.Commands.UpdatePurchaseOrderStatus;

public sealed record UpdatePurchaseOrderStatusCommand(
    int Id,
    PurchaseOrderStatus Status,
    Guid? UserId,
    string? Comments
) : IRequest<ErrorOr<PurchaseOrderDto>>;
