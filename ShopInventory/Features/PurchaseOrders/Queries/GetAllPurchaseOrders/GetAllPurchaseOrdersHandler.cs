using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetAllPurchaseOrders;

public sealed class GetAllPurchaseOrdersHandler(
    IPurchaseOrderService purchaseOrderService
) : IRequestHandler<GetAllPurchaseOrdersQuery, ErrorOr<PurchaseOrderListResponseDto>>
{
    public async Task<ErrorOr<PurchaseOrderListResponseDto>> Handle(
        GetAllPurchaseOrdersQuery request,
        CancellationToken cancellationToken)
    {
        var result = await purchaseOrderService.GetAllAsync(
            request.Page, request.PageSize, request.Status, request.CardCode,
            request.FromDate, request.ToDate, cancellationToken);
        return result;
    }
}
