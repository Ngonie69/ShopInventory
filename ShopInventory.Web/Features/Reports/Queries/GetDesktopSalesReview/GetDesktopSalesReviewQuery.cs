using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

/// <summary>The desktop sales business review for a period and, optionally, one shop.</summary>
public sealed record GetDesktopSalesReviewQuery(DateTime FromDate, DateTime ToDate, string? WarehouseCode)
    : IRequest<ErrorOr<DesktopSalesReview>>;
