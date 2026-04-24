using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.GoodsReceiptPurchaseOrders;
using ShopInventory.Services;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrderByDocEntry;

public sealed class GetGoodsReceiptPurchaseOrderByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetGoodsReceiptPurchaseOrderByDocEntryHandler> logger
) : IRequestHandler<GetGoodsReceiptPurchaseOrderByDocEntryQuery, ErrorOr<GoodsReceiptPurchaseOrderDto>>
{
    public async Task<ErrorOr<GoodsReceiptPurchaseOrderDto>> Handle(
        GetGoodsReceiptPurchaseOrderByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var goodsReceipt = await sapClient.GetGoodsReceiptPurchaseOrderByDocEntryAsync(request.DocEntry, cancellationToken);
            if (goodsReceipt is null)
                return Errors.GoodsReceiptPurchaseOrder.NotFoundByDocEntry(request.DocEntry);

            return GoodsReceiptPurchaseOrderMappings.MapFromSap(goodsReceipt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching goods receipt PO {DocEntry} from SAP", request.DocEntry);
            return Errors.GoodsReceiptPurchaseOrder.SapError(ex.Message);
        }
    }
}