using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrders;

public sealed class GetGoodsReceiptPurchaseOrdersHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetGoodsReceiptPurchaseOrdersHandler> logger
) : IRequestHandler<GetGoodsReceiptPurchaseOrdersQuery, ErrorOr<GoodsReceiptPurchaseOrderListResponseDto>>
{
    public async Task<ErrorOr<GoodsReceiptPurchaseOrderListResponseDto>> Handle(
        GetGoodsReceiptPurchaseOrdersQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            List<SAPGoodsReceiptPurchaseOrder> goodsReceipts;
            int totalCount;

            if (request.FromDate.HasValue && request.ToDate.HasValue)
            {
                goodsReceipts = await sapClient.GetGoodsReceiptPurchaseOrdersByDateRangeAsync(request.FromDate.Value, request.ToDate.Value, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(request.CardCode))
            {
                goodsReceipts = await sapClient.GetGoodsReceiptPurchaseOrdersBySupplierAsync(request.CardCode, cancellationToken);
            }
            else
            {
                goodsReceipts = await sapClient.GetPagedGoodsReceiptPurchaseOrdersAsync(request.Page, request.PageSize, cancellationToken);
                totalCount = await sapClient.GetGoodsReceiptPurchaseOrdersCountAsync(request.CardCode, request.FromDate, request.ToDate, cancellationToken);

                return new GoodsReceiptPurchaseOrderListResponseDto
                {
                    Page = request.Page,
                    PageSize = request.PageSize,
                    Count = goodsReceipts.Count,
                    TotalCount = totalCount,
                    TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                    HasMore = request.Page * request.PageSize < totalCount,
                    GoodsReceipts = goodsReceipts.Select(GoodsReceiptPurchaseOrderMappings.MapFromSap).ToList()
                };
            }

            if (!string.IsNullOrWhiteSpace(request.CardCode))
            {
                goodsReceipts = goodsReceipts
                    .Where(goodsReceipt => string.Equals(goodsReceipt.CardCode, request.CardCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            totalCount = goodsReceipts.Count;
            goodsReceipts = goodsReceipts
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToList();

            return new GoodsReceiptPurchaseOrderListResponseDto
            {
                Page = request.Page,
                PageSize = request.PageSize,
                Count = goodsReceipts.Count,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                HasMore = request.Page * request.PageSize < totalCount,
                GoodsReceipts = goodsReceipts.Select(GoodsReceiptPurchaseOrderMappings.MapFromSap).ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching goods receipt POs from SAP");
            return Errors.GoodsReceiptPurchaseOrder.LoadFailed(ex.Message);
        }
    }
}