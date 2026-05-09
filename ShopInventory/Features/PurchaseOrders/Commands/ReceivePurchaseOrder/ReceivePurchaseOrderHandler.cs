using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseOrders;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.ReceivePurchaseOrder;

public sealed class ReceivePurchaseOrderHandler(
    IPurchaseOrderService purchaseOrderService,
    IAuditService auditService,
    INotificationService notificationService,
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

            try
            {
                var statusLabel = PurchaseOrderLifecycleNotificationFactory.BuildStatusLabel(order.Status);
                var supplierDisplay = PurchaseOrderLifecycleNotificationFactory.BuildBusinessPartnerDisplay(order.CardCode, order.CardName);
                var notificationMessage = $"Goods were received against purchase order {order.OrderNumber} for {supplierDisplay}. Current status: {statusLabel}.";
                var trimmedComments = string.IsNullOrWhiteSpace(command.Request.Comments)
                    ? null
                    : command.Request.Comments.Trim();

                if (!string.IsNullOrWhiteSpace(trimmedComments))
                {
                    notificationMessage += $" Notes: {trimmedComments}";
                }

                await notificationService.CreateNotificationAsync(
                    PurchaseOrderLifecycleNotificationFactory.CreateNotification(
                        order,
                        $"Purchase Order {statusLabel}: {order.OrderNumber}",
                        notificationMessage,
                        "Success",
                        new Dictionary<string, string>
                        {
                            ["action"] = "ReceiveGoods",
                            ["comments"] = trimmedComments ?? string.Empty,
                            ["lineCount"] = command.Request.Lines.Count.ToString()
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish receiving notification for purchase order {Id}", command.Id);
            }

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
