namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

/// <summary>The periods a review is sent for, and the dates each covers.</summary>
public static class DesktopSalesReviewCadence
{
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Custom = "custom";

    public static bool IsKnown(string? cadence) => cadence is Weekly or Monthly or Custom;

    /// <summary>
    /// The last complete period of a cadence before <paramref name="todayCat"/>: the Monday-to-Sunday week
    /// or the calendar month that has fully ended.
    /// </summary>
    public static (DateTime From, DateTime To) LastComplete(string cadence, DateTime todayCat)
    {
        var today = todayCat.Date;
        if (cadence == Monthly)
        {
            var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
            return (firstOfThisMonth.AddMonths(-1), firstOfThisMonth.AddDays(-1));
        }

        // Back to the most recent Sunday strictly before today. DayOfWeek counts Sunday as 0.
        var sinceSunday = today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek;
        var sunday = today.AddDays(-sinceSunday);
        return (sunday.AddDays(-6), sunday);
    }

    public static string Title(string cadence) => cadence switch
    {
        Weekly => "Weekly sales review",
        Monthly => "Monthly sales review",
        _ => "Sales review"
    };
}
