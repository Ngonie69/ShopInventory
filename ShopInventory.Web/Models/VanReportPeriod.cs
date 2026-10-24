using System.Globalization;

namespace ShopInventory.Web.Models;

/// <summary>
/// The periods every van report offers, worked out on the CAT clock.
/// </summary>
/// <remarks>
/// A trading day belongs to the van, so "today" is CAT's whatever the server's clock says. The pages
/// each worked this out for themselves, three different ways (<c>ToCAT</c>, <c>UtcNow.AddHours(2)</c>
/// and the server's <c>DateTime.Today</c>), and the last of those put the evening on the wrong day.
/// </remarks>
public static class VanReportPeriod
{
    /// <summary>The URL's date format: a bare trading day.</summary>
    public const string QueryFormat = "yyyy-MM-dd";

    public static DateTime TodayCat => DateTime.UtcNow.AddHours(2).Date;

    /// <summary>One button on the period bar.</summary>
    public sealed record Option(string Label, DateTime From, DateTime To);

    /// <summary>
    /// The standard set — today, yesterday, this week, this month, last month — with a "Last N days"
    /// for each of <paramref name="recentDays"/>, which is how a page keeps its own default visible.
    /// </summary>
    /// <param name="wholeMonths">
    /// "This month" as the whole calendar month rather than the month so far: for a report whose
    /// target is built on all of a month's working days, which cannot say how far behind the month
    /// it is from a period that stops today.
    /// </param>
    public static IReadOnlyList<Option> Options(DateTime today, IEnumerable<int>? recentDays = null, bool wholeMonths = false)
    {
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday

        var options = new List<Option>
        {
            new("Today", today, today),
            new("Yesterday", today.AddDays(-1), today.AddDays(-1)),
            new("This week", weekStart, today)
        };

        foreach (var days in (recentDays ?? []).Where(days => days > 1).Distinct().Order())
        {
            options.Add(new($"Last {days} days", LastDays(days, today).From, today));
        }

        options.Add(new("This month", monthStart, wholeMonths ? monthStart.AddMonths(1).AddDays(-1) : today));
        options.Add(new("Last month", monthStart.AddMonths(-1), monthStart.AddDays(-1)));
        return options;
    }

    /// <summary>The <paramref name="days"/> days ending today, today included.</summary>
    public static (DateTime From, DateTime To) LastDays(int days, DateTime today) =>
        (today.AddDays(-(Math.Max(1, days) - 1)), today);

    public static DateTime? ParseDay(string? value) =>
        DateTime.TryParseExact(value, QueryFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.Date
            : null;

    public static string? FormatDay(DateTime? day) =>
        day?.ToString(QueryFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The period in words, as short as it can be said: "Fri 24 Oct 2026", "1–24 Oct 2026",
    /// "28 Sep – 24 Oct 2026".
    /// </summary>
    public static string Describe(DateTime from, DateTime to)
    {
        var culture = CultureInfo.InvariantCulture;
        from = from.Date;
        to = to.Date;

        if (from == to)
        {
            return from.ToString("ddd d MMM yyyy", culture);
        }

        if (from.Year == to.Year && from.Month == to.Month)
        {
            return $"{from.Day}–{to.ToString("d MMM yyyy", culture)}";
        }

        return from.Year == to.Year
            ? $"{from.ToString("d MMM", culture)} – {to.ToString("d MMM yyyy", culture)}"
            : $"{from.ToString("d MMM yyyy", culture)} – {to.ToString("d MMM yyyy", culture)}";
    }
}
