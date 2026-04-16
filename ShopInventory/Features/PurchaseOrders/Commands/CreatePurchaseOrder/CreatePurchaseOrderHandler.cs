using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.CreatePurchaseOrder;

public sealed class CreatePurchaseOrderHandler(
    IPurchaseOrderService purchaseOrderService,
    IAuditService auditService,
    ILogger<CreatePurchaseOrderHandler> logger
) : IRequestHandler<CreatePurchaseOrderCommand, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        CreatePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await purchaseOrderService.CreateAsync(command.Request, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.CreatePurchaseOrder, "PurchaseOrder", order.Id.ToString(), $"Purchase order created for {command.Request.CardCode}", true); } catch { }
            return order;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase order");
            return Errors.PurchaseOrder.CreationFailed(ex.Message);
        }
    }
}
