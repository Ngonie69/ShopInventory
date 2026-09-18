using ErrorOr;
using MediatR;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdateDesktopSalesReviewSchedule;

/// <summary>
/// Saves when the business review is emailed and to whom. The admin saving it becomes the account the
/// review is read as.
/// </summary>
public sealed record UpdateDesktopSalesReviewScheduleCommand(
    Guid CallerUserId,
    string? CallerName,
    bool WeeklyEnabled,
    bool MonthlyEnabled,
    List<string> Recipients
) : IRequest<ErrorOr<DesktopSalesReviewSchedule>>;
