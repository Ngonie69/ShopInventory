// Generated from ShopInventory/Features/DesktopIntegration/Queries/GetDesktopSalesReviewSchedule/DesktopSalesReviewSchedule.cs by mirror_records.py: the API records, as the Web mirrors them.
// Nullability matches the API exactly; regenerate rather than hand-edit.

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

public sealed class DesktopSalesReviewSchedule
{
    public bool WeeklyEnabled { get; set; }
    public bool MonthlyEnabled { get; set; }
    public List<string> Recipients { get; set; } = [];
    public Guid? SendAsUserId { get; set; }
    public string? SendAsUserName { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? LastWeeklyPeriodEnd { get; set; }
    public DateTime? LastMonthlyPeriodEnd { get; set; }
}
