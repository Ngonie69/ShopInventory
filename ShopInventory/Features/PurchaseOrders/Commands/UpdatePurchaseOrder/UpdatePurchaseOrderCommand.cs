using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Commands.UpdatePurchaseOrder;

public sealed record UpdatePurchaseOrderCommand(
    int Id,
    CreatePurchaseOrderRequest Request
) : IRequest<ErrorOr<PurchaseOrderDto>>;
