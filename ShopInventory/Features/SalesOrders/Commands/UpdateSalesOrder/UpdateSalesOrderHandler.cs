using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.UpdateSalesOrder;

public sealed class UpdateSalesOrderHandler(
    ISalesOrderService salesOrderService
) : IRequestHandler<UpdateSalesOrderCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        UpdateSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await salesOrderService.UpdateAsync(command.Id, command.Request, cancellationToken);
            return order;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return Errors.SalesOrder.ConcurrencyConflict;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
    }
}
