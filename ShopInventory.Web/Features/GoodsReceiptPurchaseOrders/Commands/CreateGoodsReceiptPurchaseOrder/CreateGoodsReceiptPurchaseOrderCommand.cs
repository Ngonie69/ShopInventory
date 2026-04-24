using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;

public sealed record CreateGoodsReceiptPurchaseOrderCommand(CreateGoodsReceiptPurchaseOrderRequest Request) : IRequest<ErrorOr<GoodsReceiptPurchaseOrderDto>>;