using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Merchandiser.Queries.GetMobileOrders;

public sealed record GetMobileOrdersQuery(
    Guid UserId,
    int Page,
    int PageSize,
    SalesOrderStatus? Status
) : IRequest<ErrorOr<SalesOrderListResponseDto>>;
