using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderById;

public sealed class GetPurchaseOrderByIdHandler(
    IPurchaseOrderService purchaseOrderService
) : IRequestHandler<GetPurchaseOrderByIdQuery, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        GetPurchaseOrderByIdQuery request,
        CancellationToken cancellationToken)
    {
        var order = await purchaseOrderService.GetByIdAsync(request.Id, cancellationToken);
        if (order is null)
            return Errors.PurchaseOrder.NotFound(request.Id);

        return order;
    }
}
