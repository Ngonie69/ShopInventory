using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Queries.GetSalesOrderByNumber;

public sealed class GetSalesOrderByNumberHandler(
    ISalesOrderService salesOrderService
) : IRequestHandler<GetSalesOrderByNumberQuery, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        GetSalesOrderByNumberQuery request,
        CancellationToken cancellationToken)
    {
        var order = await salesOrderService.GetByOrderNumberAsync(request.OrderNumber, cancellationToken);
        if (order is null)
            return Errors.SalesOrder.NotFoundByNumber(request.OrderNumber);

        return order;
    }
}
