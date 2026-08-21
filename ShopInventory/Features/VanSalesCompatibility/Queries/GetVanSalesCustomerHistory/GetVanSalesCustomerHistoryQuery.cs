using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomerHistory;

/// <summary>
/// What one shop on the handset's own route has bought: the sales, the orders still outstanding, and
/// the totals over a window.
/// </summary>
/// <remarks>
/// By code, for the same reason the removal is — a handset is never given the route customer id.
/// </remarks>
public sealed record GetVanSalesCustomerHistoryQuery(
    Guid UserId,
    string Code,
    DateTime? From,
    DateTime? To
) : IRequest<ErrorOr<RouteCustomerSalesDetailDto>>;
