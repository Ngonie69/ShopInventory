using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;

/// <summary>
/// The desktop sales business review for a period: the till analysis and the management report read
/// together, with the breakdowns neither carries and the findings they add up to.
/// </summary>
/// <param name="CallerUserId">Whose read scope applies — the sales list's. The scheduled email reads as the admin who switched it on.</param>
/// <param name="FromDate">First business day, inclusive. Defaults to today.</param>
/// <param name="ToDate">Last business day, inclusive. Defaults to today.</param>
/// <param name="WarehouseCode">One shop or depot's warehouse, or every one the caller may read.</param>
public sealed record GetDesktopSalesReviewQuery(
    Guid CallerUserId,
    DateTime? FromDate,
    DateTime? ToDate,
    string? WarehouseCode = null
) : IRequest<ErrorOr<DesktopSalesReview>>;
