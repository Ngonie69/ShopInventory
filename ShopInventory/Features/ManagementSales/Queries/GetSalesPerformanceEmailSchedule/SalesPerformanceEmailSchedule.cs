namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;

/// <summary>When the management sales performance email goes out, to whom, and as whom.</summary>
/// <remarks><list type="table">
/// <item><term>DailyEnabled</term><description>Every morning at 07:30 CAT, for the day before.</description></item>
/// <item><term>WeeklyEnabled</term><description>Mondays at 07:30 CAT, for the Monday-to-Sunday week just ended.</description></item>
/// <item><term>MonthlyEnabled</term><description>The 1st at 07:30 CAT, for the calendar month just ended.</description></item>
/// <item><term>SendAsUserId</term><description>The admin who last saved the schedule. Shop and vending figures are read under their scope.</description></item>
/// <item><term>LastDailyPeriodEnd</term><description>The last day a daily email went out for; a rerun for the same day sends nothing.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceEmailSchedule(
    bool DailyEnabled,
    bool WeeklyEnabled,
    bool MonthlyEnabled,
    List<string> Recipients,
    Guid? SendAsUserId,
    string? SendAsUserName,
    DateTime? UpdatedAtUtc,
    DateTime? LastDailyPeriodEnd,
    DateTime? LastWeeklyPeriodEnd,
    DateTime? LastMonthlyPeriodEnd);
