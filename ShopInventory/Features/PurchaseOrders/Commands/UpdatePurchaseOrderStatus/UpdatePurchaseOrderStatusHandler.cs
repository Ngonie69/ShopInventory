using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.UpdatePurchaseOrderStatus;

public sealed class UpdatePurchaseOrderStatusHandler(
    IPurchaseOrderService purchaseOrderService,
    ILogger<UpdatePurchaseOrderStatusHandler> logger
) : IRequestHandler<UpdatePurchaseOrderStatusCommand, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        UpdatePurchaseOrderStatusCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await purchaseOrderService.UpdateStatusAsync(
                command.Id, command.Status, command.UserId, command.Comments, cancellationToken);
            return order;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.PurchaseOrder.UpdateFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating purchase order {Id} status", command.Id);
            return Errors.PurchaseOrder.UpdateFailed(ex.Message);
        }
    }
}
