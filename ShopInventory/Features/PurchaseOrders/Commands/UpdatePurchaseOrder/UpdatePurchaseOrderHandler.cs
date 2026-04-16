using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.UpdatePurchaseOrder;

public sealed class UpdatePurchaseOrderHandler(
    IPurchaseOrderService purchaseOrderService,
    ILogger<UpdatePurchaseOrderHandler> logger
) : IRequestHandler<UpdatePurchaseOrderCommand, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        UpdatePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await purchaseOrderService.UpdateAsync(command.Id, command.Request, cancellationToken);
            return order;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.PurchaseOrder.UpdateFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating purchase order {Id}", command.Id);
            return Errors.PurchaseOrder.UpdateFailed(ex.Message);
        }
    }
}
