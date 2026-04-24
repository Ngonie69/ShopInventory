using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.GoodsReceiptPurchaseOrders;
using ShopInventory.Services;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;

public sealed class CreateGoodsReceiptPurchaseOrderHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<CreateGoodsReceiptPurchaseOrderHandler> logger
) : IRequestHandler<CreateGoodsReceiptPurchaseOrderCommand, ErrorOr<GoodsReceiptPurchaseOrderDto>>
{
    public async Task<ErrorOr<GoodsReceiptPurchaseOrderDto>> Handle(
        CreateGoodsReceiptPurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var goodsReceipt = await sapClient.CreateGoodsReceiptPurchaseOrderAsync(command.Request, cancellationToken);
            return GoodsReceiptPurchaseOrderMappings.MapFromSap(goodsReceipt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating goods receipt PO for supplier {CardCode}", command.Request.CardCode);
            return Errors.GoodsReceiptPurchaseOrder.CreationFailed(ex.Message);
        }
    }
}