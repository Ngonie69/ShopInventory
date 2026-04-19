using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.SalesOrders.Queries.GetAllSalesOrders;

public sealed record GetAllSalesOrdersQuery(
    int Page,
    int PageSize,
    SalesOrderStatus? Status,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate,
    SalesOrderSource? Source,
    string? Search = null
) : IRequest<ErrorOr<SalesOrderListResponseDto>>;
