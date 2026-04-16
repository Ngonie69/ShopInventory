using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderByNumber;

public sealed record GetPurchaseOrderByNumberQuery(string OrderNumber) : IRequest<ErrorOr<PurchaseOrderDto>>;
