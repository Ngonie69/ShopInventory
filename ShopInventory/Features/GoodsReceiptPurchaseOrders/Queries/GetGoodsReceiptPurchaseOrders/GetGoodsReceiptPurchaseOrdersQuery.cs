using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrders;

public sealed record GetGoodsReceiptPurchaseOrdersQuery(
    int Page = 1,
    int PageSize = 20,
    string? CardCode = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<GoodsReceiptPurchaseOrderListResponseDto>>;