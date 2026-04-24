using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrders;

public sealed class GetGoodsReceiptPurchaseOrdersHandler(
    IGoodsReceiptPurchaseOrderService goodsReceiptPurchaseOrderService,
    ILogger<GetGoodsReceiptPurchaseOrdersHandler> logger
) : IRequestHandler<GetGoodsReceiptPurchaseOrdersQuery, ErrorOr<GoodsReceiptPurchaseOrderListResponse>>
{
    public async Task<ErrorOr<GoodsReceiptPurchaseOrderListResponse>> Handle(
        GetGoodsReceiptPurchaseOrdersQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await goodsReceiptPurchaseOrderService.GetGoodsReceiptPurchaseOrdersAsync(
                request.Page,
                request.PageSize,
                request.CardCode,
                request.FromDate,
                request.ToDate,
                cancellationToken);

            if (response is null)
                return Errors.GoodsReceiptPurchaseOrder.LoadGoodsReceiptsFailed("Failed to load goods receipt POs.");

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading goods receipt POs in web CQRS handler");
            return Errors.GoodsReceiptPurchaseOrder.LoadGoodsReceiptsFailed("Failed to load goods receipt POs.");
        }
    }
}