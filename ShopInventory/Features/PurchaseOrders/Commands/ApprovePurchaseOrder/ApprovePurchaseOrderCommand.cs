using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Commands.ApprovePurchaseOrder;

public sealed record ApprovePurchaseOrderCommand(
    int Id,
    Guid? UserId
) : IRequest<ErrorOr<PurchaseOrderDto>>;
