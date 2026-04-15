using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.UpdateSalesOrderStatus;

public sealed class UpdateSalesOrderStatusHandler(
    ISalesOrderService salesOrderService
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
            return order;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
    }
}
