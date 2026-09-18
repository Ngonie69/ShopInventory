using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

/// <summary>The review email's schedule, as last saved from Settings.</summary>
public sealed record GetDesktopSalesReviewScheduleQuery : IRequest<ErrorOr<DesktopSalesReviewSchedule>>;
