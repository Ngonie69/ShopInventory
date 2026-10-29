using System.Globalization;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>
/// The dates the Reports Hub reads for one period-bar choice, and the period it is compared with.
/// </summary>
/// <remarks>
/// Every date is a CAT business date; the API reads both ends inclusively.
/// A single day is compared with the same weekday a week earlier, because a Monday set against the
/// Sunday before it says nothing. A week or month so far is compared with the same days of the one
/// before, so a part-month is never set against a whole one.
/// </remarks>
public sealed record ReportsPeriod(
    ReportsPeriodPreset Preset,
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    bool EndsToday)
{
    /// <summary>The fewest days the daily sales chart shows, so a one-day period still has a trend.</summary>
    public const int MinimumChartDays = 14;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>First day of the daily sales chart: the period, stretched back to two weeks when shorter.</summary>
    public DateOnly ChartFrom => Days >= MinimumChartDays ? From : To.AddDays(-(MinimumChartDays - 1));

    public string RangeLabel => FormatRange(From, To, withYear: true);

    public string PreviousLabel => FormatRange(PreviousFrom, PreviousTo, withYear: PreviousFrom.Year != To.Year);

    public static ReportsPeriod Resolve(
        ReportsPeriodPreset preset,
        DateOnly today,
        DateOnly? customFrom = null,
        DateOnly? customTo = null)
    {
        switch (preset)
        {
            case ReportsPeriodPreset.Today:
                return new(preset, today, today, today.AddDays(-7), today.AddDays(-7), true);

            case ReportsPeriodPreset.Yesterday:
                var yesterday = today.AddDays(-1);
                return new(preset, yesterday, yesterday, yesterday.AddDays(-7), yesterday.AddDays(-7), false);

            case ReportsPeriodPreset.ThisWeek:
                // Monday-start weeks, as the business reads them.
                var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                return new(preset, monday, today, monday.AddDays(-7), today.AddDays(-7), true);

            case ReportsPeriodPreset.ThisMonth:
                var first = new DateOnly(today.Year, today.Month, 1);
                var previousFirst = first.AddMonths(-1);
                var previousDay = Math.Min(today.Day, DateTime.DaysInMonth(previousFirst.Year, previousFirst.Month));
                return new(preset, first, today, previousFirst,
                    new DateOnly(previousFirst.Year, previousFirst.Month, previousDay), true);

            case ReportsPeriodPreset.Last30Days:
                return new(preset, today.AddDays(-29), today, today.AddDays(-59), today.AddDays(-30), true);

            default:
                var from = customFrom ?? today.AddDays(-29);
                var to = customTo ?? today;
                if (to < from)
                {
                    (from, to) = (to, from);
                }

                if (to > today)
                {
                    to = today;
                }

                if (from > to)
                {
                    from = to;
                }

                var length = to.DayNumber - from.DayNumber + 1;
                return new(ReportsPeriodPreset.Custom, from, to, from.AddDays(-length), from.AddDays(-1), to == today);
        }
    }

    /// <summary>The period's name in a /reports address: <c>?period=month</c>.</summary>
    public string Slug => SlugFor(Preset);

    /// <summary>The query string that reopens this period: <c>period=month&amp;from=…&amp;to=…</c>.</summary>
    public string Query => $"period={Slug}&from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}";

    public static string SlugFor(ReportsPeriodPreset preset) => preset switch
    {
        ReportsPeriodPreset.Today => "today",
        ReportsPeriodPreset.Yesterday => "yesterday",
        ReportsPeriodPreset.ThisWeek => "week",
        ReportsPeriodPreset.ThisMonth => "month",
        ReportsPeriodPreset.Last30Days => "30d",
        _ => "custom"
    };

    /// <summary>
    /// The period a /reports address names. A named period is worked out afresh from today, so a
    /// bookmarked "this month" stays this month; only <c>custom</c> reads the dates. Anything
    /// unreadable falls back to this month.
    /// </summary>
    public static ReportsPeriod FromQuery(string? period, string? from, string? to, DateOnly today)
    {
        var preset = Enum.GetValues<ReportsPeriodPreset>()
            .Where(candidate => string.Equals(SlugFor(candidate), period, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => (ReportsPeriodPreset?)candidate)
            .FirstOrDefault();

        if (preset is null && (from is not null || to is not null))
        {
            preset = ReportsPeriodPreset.Custom;
        }

        if (preset == ReportsPeriodPreset.Custom)
        {
            var customFrom = ParseDate(from);
            var customTo = ParseDate(to);
            if (customFrom is null && customTo is null)
            {
                return Resolve(ReportsPeriodPreset.ThisMonth, today);
            }

            return Resolve(ReportsPeriodPreset.Custom, today, customFrom ?? customTo, customTo ?? customFrom);
        }

        return Resolve(preset ?? ReportsPeriodPreset.ThisMonth, today);
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static string FormatRange(DateOnly from, DateOnly to, bool withYear)
    {
        var year = withYear ? " yyyy" : string.Empty;
        if (from == to)
        {
            return to.ToString("ddd d MMM" + year, Culture);
        }

        if (from.Year != to.Year)
        {
            return $"{from.ToString("d MMM yyyy", Culture)} – {to.ToString("d MMM yyyy", Culture)}";
        }

        return from.Month == to.Month
            ? $"{from.Day} – {to.ToString("d MMM" + year, Culture)}"
            : $"{from.ToString("d MMM", Culture)} – {to.ToString("d MMM" + year, Culture)}";
    }
}
