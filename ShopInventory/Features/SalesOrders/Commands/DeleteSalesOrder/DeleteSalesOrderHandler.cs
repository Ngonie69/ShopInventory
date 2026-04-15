using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.DeleteSalesOrder;

public sealed class DeleteSalesOrderHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService
) : IRequestHandler<DeleteSalesOrderCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await salesOrderService.DeleteAsync(command.Id, cancellationToken);
            if (!deleted)
                return Errors.SalesOrder.NotFound(command.Id);

            try { await auditService.LogAsync(AuditActions.DeleteSalesOrder, "SalesOrder", command.Id.ToString(), $"Sales order {command.Id} deleted", true); } catch { }
            return Result.Deleted;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
    }
}
