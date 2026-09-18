using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.Reports.Commands.DesktopSalesReviewEmail;

/// <summary>Switches the weekly and monthly review emails on or off and sets who receives them.</summary>
public sealed record UpdateDesktopSalesReviewScheduleCommand(bool WeeklyEnabled, bool MonthlyEnabled, List<string> Recipients)
    : IRequest<ErrorOr<DesktopSalesReviewSchedule>>;
