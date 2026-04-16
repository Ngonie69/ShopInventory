using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Commands.ReceivePurchaseOrder;

public sealed record ReceivePurchaseOrderCommand(
    int Id,
    ReceivePurchaseOrderRequest Request,
    Guid? UserId
) : IRequest<ErrorOr<PurchaseOrderDto>>;
