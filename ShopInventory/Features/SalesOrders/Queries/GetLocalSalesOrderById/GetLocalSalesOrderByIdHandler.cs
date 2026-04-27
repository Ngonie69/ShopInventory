using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Queries.GetLocalSalesOrderById;

public sealed class GetLocalSalesOrderByIdHandler(
    ISalesOrderService salesOrderService
) : IRequestHandler<GetLocalSalesOrderByIdQuery, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        GetLocalSalesOrderByIdQuery request,
        CancellationToken cancellationToken)
    {
        var order = await salesOrderService.GetByIdFromLocalAsync(request.Id, cancellationToken);
        if (order is null)
            return Errors.SalesOrder.NotFound(request.Id);

        return order;
    }
}
