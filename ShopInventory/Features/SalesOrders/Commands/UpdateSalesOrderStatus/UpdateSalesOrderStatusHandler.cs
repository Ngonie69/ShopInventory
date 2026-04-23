using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.UpdateSalesOrderStatus;

public sealed class UpdateSalesOrderStatusHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService
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
