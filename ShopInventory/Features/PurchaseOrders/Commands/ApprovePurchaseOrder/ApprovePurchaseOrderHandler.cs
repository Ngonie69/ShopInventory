using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseOrders;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.ApprovePurchaseOrder;

public sealed class ApprovePurchaseOrderHandler(
    IPurchaseOrderService purchaseOrderService,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<ApprovePurchaseOrderHandler> logger
) : IRequestHandler<ApprovePurchaseOrderCommand, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        ApprovePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await purchaseOrderService.ApproveAsync(command.Id, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.ApprovePurchaseOrder, "PurchaseOrder", command.Id.ToString(), $"Purchase order {command.Id} approved", true); } catch { }

            try
            {
                var supplierDisplay = PurchaseOrderLifecycleNotificationFactory.BuildBusinessPartnerDisplay(order.CardCode, order.CardName);
                var totalDisplay = PurchaseOrderLifecycleNotificationFactory.BuildMoneyDisplay(order.Currency, order.DocTotal);

                await notificationService.CreateNotificationAsync(
                    PurchaseOrderLifecycleNotificationFactory.CreateNotification(
                        order,
                        $"Purchase Order Approved: {order.OrderNumber}",
                        $"Purchase order {order.OrderNumber} for {supplierDisplay} totaling {totalDisplay} was approved.",
                        "Success",
                        new Dictionary<string, string>
                        {
                            ["action"] = "Approved"
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish approval notification for purchase order {Id}", command.Id);
            }

            return order;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.PurchaseOrder.ApprovalFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error approving purchase order {Id}", command.Id);
            return Errors.PurchaseOrder.ApprovalFailed(ex.Message);
        }
    }
}
