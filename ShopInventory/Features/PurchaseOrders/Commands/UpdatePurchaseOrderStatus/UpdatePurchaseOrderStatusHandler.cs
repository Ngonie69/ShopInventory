using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseOrders;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.UpdatePurchaseOrderStatus;

public sealed class UpdatePurchaseOrderStatusHandler(
    IPurchaseOrderService purchaseOrderService,
    INotificationService notificationService,
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

            try
            {
                var statusLabel = PurchaseOrderLifecycleNotificationFactory.BuildStatusLabel(order.Status);
                var supplierDisplay = PurchaseOrderLifecycleNotificationFactory.BuildBusinessPartnerDisplay(order.CardCode, order.CardName);
                var notificationMessage = $"Purchase order {order.OrderNumber} for {supplierDisplay} was moved to {statusLabel}.";
                var trimmedComments = string.IsNullOrWhiteSpace(command.Comments)
                    ? null
                    : command.Comments.Trim();

                if (!string.IsNullOrWhiteSpace(trimmedComments))
                {
                    notificationMessage += $" Reason: {trimmedComments}";
                }

                await notificationService.CreateNotificationAsync(
                    PurchaseOrderLifecycleNotificationFactory.CreateNotification(
                        order,
                        $"Purchase Order {statusLabel}: {order.OrderNumber}",
                        notificationMessage,
                        PurchaseOrderLifecycleNotificationFactory.GetTypeForStatus(order.Status),
                        new Dictionary<string, string>
                        {
                            ["action"] = order.Status.ToString(),
                            ["comments"] = trimmedComments ?? string.Empty
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish status notification for purchase order {Id}", command.Id);
            }

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
