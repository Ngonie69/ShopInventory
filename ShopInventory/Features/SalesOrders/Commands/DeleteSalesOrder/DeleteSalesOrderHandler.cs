using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.SalesOrders;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.DeleteSalesOrder;

public sealed class DeleteSalesOrderHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<DeleteSalesOrderHandler> logger
) : IRequestHandler<DeleteSalesOrderCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await salesOrderService.GetByIdFromLocalAsync(command.Id, cancellationToken);
            if (order == null)
                return Errors.SalesOrder.NotFound(command.Id);

            var deleted = await salesOrderService.DeleteAsync(command.Id, cancellationToken);
            if (!deleted)
                return Errors.SalesOrder.NotFound(command.Id);

            if (order.Source == SalesOrderSource.Mobile)
            {
                var customerName = string.IsNullOrWhiteSpace(order.CardName)
                    ? order.CardCode
                    : order.CardName;
                var notificationTitle = $"Sales Order Deleted: {order.OrderNumber}";
                var creatorNotificationMessage = $"Your mobile sales order {order.OrderNumber} for {customerName} was deleted.";
                var staffNotificationMessage = $"Mobile sales order {order.OrderNumber} for {customerName} was deleted.";
                var notificationData = new Dictionary<string, string>
                {
                    ["action"] = "Deleted",
                    ["status"] = "Deleted"
                };

                var creatorNotification = SalesOrderLifecycleNotificationFactory.CreateCreatorNotification(
                    order,
                    notificationTitle,
                    creatorNotificationMessage,
                    "Warning",
                    notificationData);

                if (creatorNotification is not null)
                {
                    try
                    {
                        await notificationService.CreateNotificationAsync(creatorNotification, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Failed to notify creator {Username} about deleted mobile sales order {OrderId}",
                            order.CreatedByUserName,
                            order.Id);
                    }
                }

                try
                {
                    foreach (var staffNotification in SalesOrderLifecycleNotificationFactory.CreateStaffNotifications(
                                 order,
                                 notificationTitle,
                                 staffNotificationMessage,
                                 "Warning",
                                 notificationData))
                    {
                        await notificationService.CreateNotificationAsync(staffNotification, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to publish staff deletion notifications for mobile sales order {OrderId}",
                        order.Id);
                }
            }

            try { await auditService.LogAsync(AuditActions.DeleteSalesOrder, "SalesOrder", command.Id.ToString(), $"Sales order {command.Id} deleted", true); } catch { }
            return Result.Deleted;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
    }
}
