using ErrorOr;
using MediatR;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesOrdersForRoute;

/// <summary>
/// The customer orders a van is expected to carry — its load list.
/// </summary>
/// <remarks>
/// Staff-facing, so unlike the customer queries this one takes the filters from the caller. It is
/// the answer to "what has been ordered for Tuesday's Guruve run?", which is the question the depot
/// asks the afternoon before.
/// </remarks>
public sealed record GetVanSalesOrdersForRouteQuery(
    string? AssignedBusinessPartnerCode,
    string? RouteCode,
    DateTime? VisitDate,
    VanSalesOrderStatus? Status
) : IRequest<ErrorOr<VanSalesRouteLoadResult>>;
