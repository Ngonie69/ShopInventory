using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetAllPurchaseOrders;

public sealed record GetAllPurchaseOrdersQuery(
    int Page,
    int PageSize,
    PurchaseOrderStatus? Status,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<PurchaseOrderListResponseDto>>;
