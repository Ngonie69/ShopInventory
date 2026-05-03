using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
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

            if (order.Source == SalesOrderSource.Mobile && !string.IsNullOrWhiteSpace(order.CreatedByUserName))
            {
                try
                {
                    var customerName = string.IsNullOrWhiteSpace(order.CardName)
                        ? order.CardCode
                        : order.CardName;

                    await notificationService.CreateNotificationAsync(new CreateNotificationRequest
                    {
                        Title = $"Sales Order Deleted: {order.OrderNumber}",
                        Message = $"Your mobile sales order {order.OrderNumber} for {customerName} was deleted.",
                        Type = "Warning",
                        Category = "SalesOrder",
                        EntityType = "SalesOrder",
                        EntityId = order.OrderNumber,
                        ActionUrl = "/mobile-drafts",
                        TargetUsername = order.CreatedByUserName,
                        Data = new Dictionary<string, string>
                        {
                            ["orderId"] = order.Id.ToString(),
                            ["orderNumber"] = order.OrderNumber,
                            ["action"] = "Deleted",
                            ["status"] = "Deleted"
                        }
                    }, cancellationToken);
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

            try { await auditService.LogAsync(AuditActions.DeleteSalesOrder, "SalesOrder", command.Id.ToString(), $"Sales order {command.Id} deleted", true); } catch { }
            return Result.Deleted;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
    }
}
