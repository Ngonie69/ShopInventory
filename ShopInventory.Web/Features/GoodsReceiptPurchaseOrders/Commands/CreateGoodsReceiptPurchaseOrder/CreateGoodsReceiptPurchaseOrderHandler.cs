using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;

public sealed class CreateGoodsReceiptPurchaseOrderHandler(
    IGoodsReceiptPurchaseOrderService goodsReceiptPurchaseOrderService,
    ILogger<CreateGoodsReceiptPurchaseOrderHandler> logger
) : IRequestHandler<CreateGoodsReceiptPurchaseOrderCommand, ErrorOr<GoodsReceiptPurchaseOrderDto>>
{
    public async Task<ErrorOr<GoodsReceiptPurchaseOrderDto>> Handle(
        CreateGoodsReceiptPurchaseOrderCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdGoodsReceipt = await goodsReceiptPurchaseOrderService.CreateGoodsReceiptPurchaseOrderAsync(request.Request, cancellationToken);

            if (createdGoodsReceipt is null)
                return Errors.GoodsReceiptPurchaseOrder.CreateGoodsReceiptFailed("Failed to create goods receipt PO.");

            return createdGoodsReceipt;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Goods receipt PO creation request failed");
            return Errors.GoodsReceiptPurchaseOrder.CreateGoodsReceiptFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error creating goods receipt PO");
            return Errors.GoodsReceiptPurchaseOrder.CreateGoodsReceiptFailed("Failed to create goods receipt PO.");
        }
    }
}