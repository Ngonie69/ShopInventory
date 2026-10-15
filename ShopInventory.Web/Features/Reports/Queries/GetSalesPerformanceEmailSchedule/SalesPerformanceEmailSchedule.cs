// Mirrors ShopInventory/Features/ManagementSales/Queries/GetSalesPerformanceEmailSchedule/SalesPerformanceEmailSchedule.cs.
// Nullability matches the API exactly.

namespace ShopInventory.Web.Features.Reports.Queries.GetSalesPerformanceEmailSchedule;

public sealed class SalesPerformanceEmailSchedule
{
    public bool DailyEnabled { get; set; }
    public bool WeeklyEnabled { get; set; }
    public bool MonthlyEnabled { get; set; }
    public List<string> Recipients { get; set; } = [];
    public Guid? SendAsUserId { get; set; }
    public string? SendAsUserName { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? LastDailyPeriodEnd { get; set; }
    public DateTime? LastWeeklyPeriodEnd { get; set; }
    public DateTime? LastMonthlyPeriodEnd { get; set; }
}
