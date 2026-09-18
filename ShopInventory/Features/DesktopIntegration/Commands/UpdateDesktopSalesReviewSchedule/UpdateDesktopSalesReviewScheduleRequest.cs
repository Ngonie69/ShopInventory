namespace ShopInventory.Features.DesktopIntegration.Commands.UpdateDesktopSalesReviewSchedule;

/// <summary>The body of <c>PUT api/DesktopIntegration/sales/review/schedule</c>.</summary>
public sealed record UpdateDesktopSalesReviewScheduleRequest(bool WeeklyEnabled, bool MonthlyEnabled, List<string>? Recipients);
