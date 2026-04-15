using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Queries.GetSalesOrderById;

public sealed class GetSalesOrderByIdHandler(
    ISalesOrderService salesOrderService
) : IRequestHandler<GetSalesOrderByIdQuery, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        GetSalesOrderByIdQuery request,
        CancellationToken cancellationToken)
    {
        var order = await salesOrderService.GetByIdAsync(request.Id, cancellationToken);
        if (order is null)
            return Errors.SalesOrder.NotFound(request.Id);

        return order;
    }
}
