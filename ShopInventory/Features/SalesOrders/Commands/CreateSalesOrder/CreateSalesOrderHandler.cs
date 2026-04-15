using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.CreateSalesOrder;

public sealed class CreateSalesOrderHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService,
    ILogger<CreateSalesOrderHandler> logger
) : IRequestHandler<CreateSalesOrderCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        CreateSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await salesOrderService.CreateAsync(command.Request, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.CreateSalesOrder, "SalesOrder", order.Id.ToString(), $"Sales order created for {command.Request.CardCode}", true); } catch { }
            return order;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating sales order");
            var message = ex.InnerException?.Message ?? ex.Message;
            return Errors.SalesOrder.CreationFailed(message);
        }
    }
}
