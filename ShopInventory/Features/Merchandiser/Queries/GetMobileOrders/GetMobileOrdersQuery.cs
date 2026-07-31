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
    string? Search,
    // Restricts the list to one customer. Search does not cover card codes, so without this the
    // app had to page through a rep's whole order history and filter on the device.
    string? CardCode = null
) : IRequest<ErrorOr<SalesOrderListResponseDto>>;
