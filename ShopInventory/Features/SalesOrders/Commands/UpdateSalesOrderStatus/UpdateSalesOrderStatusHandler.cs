using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.UpdateSalesOrderStatus;

public sealed class UpdateSalesOrderStatusHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<UpdateSalesOrderStatusHandler> logger
) : IRequestHandler<UpdateSalesOrderStatusCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        UpdateSalesOrderStatusCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await salesOrderService.UpdateStatusAsync(
                command.Id, command.Status, command.UserId, command.Comments, cancellationToken);

            if (order.Source == SalesOrderSource.Mobile
                && order.Status == SalesOrderStatus.Cancelled
                && !string.IsNullOrWhiteSpace(order.CreatedByUserName))
            {
                try
                {
                    var customerName = string.IsNullOrWhiteSpace(order.CardName)
                        ? order.CardCode
                        : order.CardName;

                    await notificationService.CreateNotificationAsync(new CreateNotificationRequest
                    {
                        Title = $"Sales Order Cancelled: {order.OrderNumber}",
                        Message = $"Your mobile sales order {order.OrderNumber} for {customerName} was cancelled.",
                        Type = "Warning",
                        Category = "SalesOrder",
                        EntityType = "SalesOrder",
                        EntityId = order.OrderNumber,
                        ActionUrl = "/mobile-drafts",
                        TargetUserId = order.CreatedByUserId,
                        TargetUsername = order.CreatedByUserName,
                        Data = new Dictionary<string, string>
                        {
                            ["orderId"] = order.Id.ToString(),
                            ["orderNumber"] = order.OrderNumber,
                            ["status"] = order.Status.ToString()
                        }
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to notify creator {Username} about cancelled mobile sales order {OrderId}",
                        order.CreatedByUserName,
                        order.Id);
                }
            }

            try
            {
                await auditService.LogAsync(
                    AuditActions.UpdateSalesOrder,
                    "SalesOrder",
                    command.Id.ToString(),
                    $"Sales order {command.Id} status updated to {order.Status}",
                    true);
            }
            catch
            {
            }
            return order;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
    }
}
