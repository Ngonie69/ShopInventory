using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesStockPosition;

/// <summary>
/// What the van this account drives is carrying now, rebuilt from what the platform has been told.
/// </summary>
/// <remarks>
/// The read half of <c>POST stock/position</c>, and the only way a van can ask rather than only tell.
/// Takes no warehouse and accepts none: the van is resolved from the signed-in account through the
/// same resolver the post uses, so a handset cannot ask about a van it does not drive.
/// </remarks>
public sealed record GetVanSalesStockPositionQuery(Guid UserId)
    : IRequest<ErrorOr<VanSalesStockPositionResult>>;
