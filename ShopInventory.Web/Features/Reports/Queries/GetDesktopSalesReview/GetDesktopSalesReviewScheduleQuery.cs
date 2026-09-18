using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

/// <summary>When the review is emailed and to whom.</summary>
public sealed record GetDesktopSalesReviewScheduleQuery : IRequest<ErrorOr<DesktopSalesReviewSchedule>>;
