using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Commands.CreatePurchaseOrder;

public sealed record CreatePurchaseOrderCommand(
    CreatePurchaseOrderRequest Request,
    Guid? UserId
) : IRequest<ErrorOr<PurchaseOrderDto>>;
