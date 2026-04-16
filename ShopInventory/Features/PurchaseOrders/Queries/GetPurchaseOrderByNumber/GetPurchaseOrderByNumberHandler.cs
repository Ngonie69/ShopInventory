using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderByNumber;

public sealed class GetPurchaseOrderByNumberHandler(
    IPurchaseOrderService purchaseOrderService
) : IRequestHandler<GetPurchaseOrderByNumberQuery, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        GetPurchaseOrderByNumberQuery request,
        CancellationToken cancellationToken)
    {
        var order = await purchaseOrderService.GetByOrderNumberAsync(request.OrderNumber, cancellationToken);
        if (order is null)
            return Errors.PurchaseOrder.NotFoundByNumber(request.OrderNumber);

        return order;
    }
}
