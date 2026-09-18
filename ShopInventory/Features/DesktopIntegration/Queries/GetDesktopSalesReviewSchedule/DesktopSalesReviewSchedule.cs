namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

/// <summary>When the business review is emailed, to whom, and as whom.</summary>
/// <remarks><list type="table">
/// <item><term>WeeklyEnabled</term><description>Mondays at 07:00 CAT, for the Monday-to-Sunday week just ended.</description></item>
/// <item><term>MonthlyEnabled</term><description>The 1st at 07:00 CAT, for the calendar month just ended.</description></item>
/// <item><term>SendAsUserId</term><description>The admin who last saved the schedule. The review is read under their scope, and stops if their account is disabled.</description></item>
/// <item><term>LastWeeklyPeriodEnd</term><description>The last week-end a weekly review went out for; a rerun for the same week sends nothing.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewSchedule(
    bool WeeklyEnabled,
    bool MonthlyEnabled,
    List<string> Recipients,
    Guid? SendAsUserId,
    string? SendAsUserName,
    DateTime? UpdatedAtUtc,
    DateTime? LastWeeklyPeriodEnd,
    DateTime? LastMonthlyPeriodEnd);
