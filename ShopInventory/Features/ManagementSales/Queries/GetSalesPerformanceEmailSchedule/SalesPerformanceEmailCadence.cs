using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;

/// <summary>The periods the management email is sent for, and the dates each covers.</summary>
public static class SalesPerformanceEmailCadence
{
    public const string Daily = "daily";
    public const string Weekly = DesktopSalesReviewCadence.Weekly;
    public const string Monthly = DesktopSalesReviewCadence.Monthly;
    public const string Custom = DesktopSalesReviewCadence.Custom;

    /// <summary>The scheduled cadences, in the order the job tries them.</summary>
    public static readonly string[] Scheduled = [Daily, Weekly, Monthly];

    public static bool IsKnown(string? cadence) => cadence is Daily or Weekly or Monthly or Custom;

    /// <summary>
    /// The last complete period before <paramref name="todayCat"/>: yesterday, the Monday-to-Sunday week
    /// just ended, or the calendar month just ended — the review's weeks and months exactly.
    /// </summary>
    public static (DateTime From, DateTime To) LastComplete(string cadence, DateTime todayCat)
    {
        if (cadence == Daily)
        {
            var yesterday = todayCat.Date.AddDays(-1);
            return (yesterday, yesterday);
        }

        return DesktopSalesReviewCadence.LastComplete(cadence, todayCat);
    }

    public static string Title(string cadence) => cadence switch
    {
        Daily => "Daily sales performance",
        Weekly => "Weekly sales performance",
        Monthly => "Monthly sales performance",
        _ => "Sales performance"
    };

    /// <summary>What the comparison period is called in the email.</summary>
    public static string PreviousLabel(string cadence) => cadence switch
    {
        Daily => "the day before",
        Weekly => "the week before",
        Monthly => "the month before",
        _ => "the same number of days before"
    };
}
