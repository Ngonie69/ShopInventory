using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrders;

public sealed record GetGoodsReceiptPurchaseOrdersQuery(
    int Page = 1,
    int PageSize = 20,
    string? CardCode = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<GoodsReceiptPurchaseOrderListResponse>>;