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
    IAuditService auditService
) : IRequestHandler<ApproveSalesOrderCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        ApproveSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await salesOrderService.ApproveAsync(command.Id, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.ApproveSalesOrder, "SalesOrder", command.Id.ToString(), $"Sales order {command.Id} approved", true); } catch { }
            return order;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
    }
}
