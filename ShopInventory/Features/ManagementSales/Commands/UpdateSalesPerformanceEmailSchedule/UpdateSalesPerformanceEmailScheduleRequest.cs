namespace ShopInventory.Features.ManagementSales.Commands.UpdateSalesPerformanceEmailSchedule;

/// <summary>The body of <c>PUT api/Report/sales-performance/schedule</c>.</summary>
public sealed record UpdateSalesPerformanceEmailScheduleRequest(bool DailyEnabled, bool WeeklyEnabled, bool MonthlyEnabled, List<string>? Recipients);
