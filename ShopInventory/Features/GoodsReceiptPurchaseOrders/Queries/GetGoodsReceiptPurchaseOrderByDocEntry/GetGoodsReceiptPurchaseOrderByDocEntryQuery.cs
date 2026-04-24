using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrderByDocEntry;

public sealed record GetGoodsReceiptPurchaseOrderByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<GoodsReceiptPurchaseOrderDto>>;