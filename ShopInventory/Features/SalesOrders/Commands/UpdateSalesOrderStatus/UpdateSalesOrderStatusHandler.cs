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
                && (order.Status == SalesOrderStatus.Cancelled || order.Status == SalesOrderStatus.Rejected)
                && !string.IsNullOrWhiteSpace(order.CreatedByUserName))
            {
                try
                {
                    var customerName = string.IsNullOrWhiteSpace(order.CardName)
                        ? order.CardCode
                        : order.CardName;
                    var isRejected = order.Status == SalesOrderStatus.Rejected;
                    var notificationTitle = isRejected
                        ? $"Sales Order Rejected: {order.OrderNumber}"
                        : $"Sales Order Cancelled: {order.OrderNumber}";
                    var statusVerb = isRejected ? "rejected" : "cancelled";
                    var trimmedComments = string.IsNullOrWhiteSpace(command.Comments)
                        ? null
                        : command.Comments.Trim();
                    var notificationMessage = $"Your mobile sales order {order.OrderNumber} for {customerName} was {statusVerb}.";

                    if (!string.IsNullOrWhiteSpace(trimmedComments))
                        notificationMessage += $" Reason: {trimmedComments}";

                    await notificationService.CreateNotificationAsync(new CreateNotificationRequest
                    {
                        Title = notificationTitle,
                        Message = notificationMessage,
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
                            ["cardCode"] = order.CardCode,
                            ["customerCode"] = order.CardCode,
                            ["customerName"] = customerName,
                            ["status"] = order.Status.ToString(),
                            ["action"] = order.Status.ToString(),
                            ["comments"] = trimmedComments ?? string.Empty
                        }
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to notify creator {Username} about {Status} mobile sales order {OrderId}",
                        order.CreatedByUserName,
                        order.Status,
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
