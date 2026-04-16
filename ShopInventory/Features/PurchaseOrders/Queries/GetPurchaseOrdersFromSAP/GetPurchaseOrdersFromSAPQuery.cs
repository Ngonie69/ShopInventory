using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrdersFromSAP;

public sealed record GetPurchaseOrdersFromSAPQuery(
    int Page,
    int PageSize,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<PurchaseOrderListResponseDto>>;
