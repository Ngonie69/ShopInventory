using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;

public sealed record CreateGoodsReceiptPurchaseOrderCommand(CreateGoodsReceiptPurchaseOrderRequest Request) : IRequest<ErrorOr<GoodsReceiptPurchaseOrderDto>>;