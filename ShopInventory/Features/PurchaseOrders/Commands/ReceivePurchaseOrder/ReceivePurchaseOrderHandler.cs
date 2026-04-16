using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.ReceivePurchaseOrder;

public sealed class ReceivePurchaseOrderHandler(
    IPurchaseOrderService purchaseOrderService,
    IAuditService auditService,
    ILogger<ReceivePurchaseOrderHandler> logger
) : IRequestHandler<ReceivePurchaseOrderCommand, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        ReceivePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await purchaseOrderService.ReceiveItemsAsync(command.Id, command.Request, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.ReceiveGoods, "PurchaseOrder", command.Id.ToString(), $"Goods received for purchase order {command.Id}", true); } catch { }
            return order;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.PurchaseOrder.ReceiveFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error receiving items for purchase order {Id}", command.Id);
            return Errors.PurchaseOrder.ReceiveFailed(ex.Message);
        }
    }
}
