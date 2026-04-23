using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.ApproveSalesOrder;

public sealed class ApproveSalesOrderHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<ApproveSalesOrderHandler> logger
) : IRequestHandler<ApproveSalesOrderCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        ApproveSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await salesOrderService.ApproveAsync(command.Id, command.UserId, cancellationToken);
            if (order.Source == SalesOrderSource.Mobile && !string.IsNullOrWhiteSpace(order.CreatedByUserName))
            {
                try
                {
                    var notificationMessage = order.SAPDocNum.HasValue
                        ? $"Your mobile sales order {order.OrderNumber} for {order.CardName ?? order.CardCode} was approved and posted to SAP as document #{order.SAPDocNum}."
                        : $"Your mobile sales order {order.OrderNumber} for {order.CardName ?? order.CardCode} was approved successfully.";

                    await notificationService.CreateNotificationAsync(new CreateNotificationRequest
                    {
                        Title = $"Sales Order Approved: {order.OrderNumber}",
                        Message = notificationMessage,
                        Type = "Success",
                        Category = "SalesOrder",
                        EntityType = "SalesOrder",
                        EntityId = order.OrderNumber,
                        ActionUrl = "/mobile-drafts",
                        TargetUsername = order.CreatedByUserName
                    }, cancellationToken);
                }
                catch
                {
                }
            }

            try
            {
                var auditMessage = order.IsSynced && order.SAPDocNum.HasValue
                    ? $"Sales order {command.Id} approved and posted to SAP as Doc #{order.SAPDocNum}"
                    : $"Sales order {command.Id} approved";
                await auditService.LogAsync(AuditActions.ApproveSalesOrder, "SalesOrder", command.Id.ToString(), auditMessage, true);
            }
            catch
            {
            }
            return order;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to approve sales order {OrderId} for user {UserId}", command.Id, command.UserId);
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error approving sales order {OrderId} for user {UserId}", command.Id, command.UserId);
            return Errors.SalesOrder.ApprovalFailed(ex.GetBaseException().Message);
        }
    }
}
