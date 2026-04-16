using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.DeletePurchaseOrder;

public sealed class DeletePurchaseOrderHandler(
    IPurchaseOrderService purchaseOrderService,
    IAuditService auditService,
    ILogger<DeletePurchaseOrderHandler> logger
) : IRequestHandler<DeletePurchaseOrderCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeletePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await purchaseOrderService.DeleteAsync(command.Id, cancellationToken);
            if (!deleted)
                return Errors.PurchaseOrder.NotFound(command.Id);

            try { await auditService.LogAsync(AuditActions.DeletePurchaseOrder, "PurchaseOrder", command.Id.ToString(), $"Purchase order {command.Id} deleted", true); } catch { }
            return Result.Deleted;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.PurchaseOrder.DeleteFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting purchase order {Id}", command.Id);
            return Errors.PurchaseOrder.DeleteFailed(ex.Message);
        }
    }
}
