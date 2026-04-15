using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Queries.GetAllSalesOrders;

public sealed class GetAllSalesOrdersHandler(
    ISalesOrderService salesOrderService
) : IRequestHandler<GetAllSalesOrdersQuery, ErrorOr<SalesOrderListResponseDto>>
{
    public async Task<ErrorOr<SalesOrderListResponseDto>> Handle(
        GetAllSalesOrdersQuery request,
        CancellationToken cancellationToken)
    {
        var result = await salesOrderService.GetAllAsync(
            request.Page, request.PageSize, request.Status, request.CardCode,
            request.FromDate, request.ToDate, request.Source, cancellationToken);
        return result;
    }
}
