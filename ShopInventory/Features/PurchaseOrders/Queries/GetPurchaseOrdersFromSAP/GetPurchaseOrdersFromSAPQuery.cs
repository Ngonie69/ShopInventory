using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrdersFromSAP;

public sealed record GetPurchaseOrdersFromSAPQuery(
    int Page,
    int PageSize,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate,
    PurchaseOrderStatus? Status = null,
    bool IncludeSummary = false
) : IRequest<ErrorOr<PurchaseOrderListResponseDto>>;
