using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Merchandiser.Queries.GetMobileOrders;

public sealed record GetMobileOrdersQuery(
    Guid UserId,
    int Page,
    int PageSize,
    SalesOrderStatus? Status,
    DateTime? FromDate,
    DateTime? ToDate,
    string? Search
) : IRequest<ErrorOr<SalesOrderListResponseDto>>;
