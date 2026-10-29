using System.Globalization;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.VanPerformance;

/// <summary>One bar in a horizontal bar list: a route, a rep, an item.</summary>
public sealed record VspBarRow(string Label, decimal Value, string ValueText)
{
    /// <summary>A second line under the label — a territory, a rep's route.</summary>
    public string? Sub { get; init; }

    /// <summary>Drawn in the faint tone: sales that cannot be attributed, not a fault.</summary>
    public bool Muted { get; init; }

    /// <summary>Small figures in their own columns after the bar, each with its own heading.</summary>
    public IReadOnlyList<VspFigure> Extras { get; init; } = [];

    /// <summary>A rate drawn as a meter beside the bar; null draws an em dash, never an empty meter.</summary>
    public double? Meter { get; init; }
}

/// <summary>A figure in a bar row's extra column. <c>None</c> renders muted: a rate with no denominator.</summary>
public sealed record VspFigure(string Text, bool None = false, bool Low = false);

/// <summary>One column of a column chart: a day, a weekday, a drop-size band.</summary>
public sealed record VspColumn(string Label, decimal? Value, string ValueText, string Tip)
{
    /// <summary>A second series drawn as a line over the columns — productive calls on a day.</summary>
    public double? Line { get; init; }

    /// <summary>Darker than its neighbours: the busiest weekday, the middle half of the drops.</summary>
    public bool Emphasis { get; init; }

    /// <summary>Whether the axis label is drawn. Long periods label every few days.</summary>
    public bool ShowLabel { get; init; } = true;

    /// <summary>Weekday starts get a heavier label so a month reads in weeks.</summary>
    public bool StrongLabel { get; init; }
}

/// <summary>One item's unit-price spread against its own weighted average.</summary>
public sealed record VspPriceRange(
    string Label,
    string Sub,
    double LowPercent,
    double HighPercent,
    string Low,
    string Average,
    string High,
    string Spread,
    bool WideSpread,
    string? Cheapest);

/// <summary>
/// Turns the performance report into chart rows for one currency at a time.
/// </summary>
/// <remarks>
/// Every chart here plots a single currency. The report keeps money per currency because a van that
/// took USD 40 and ZWG 900 took neither 940 nor USD 940; a bar that stacked the two would be that
/// sum drawn instead of printed. So the caller picks a currency, and rows with nothing in it are left
/// out rather than drawn as zero.
///
/// A day nothing sold is kept, with no value: the chart draws it as a tick, because the gap between
/// two trading days is information about the round. A rate with no denominator stays null all the
/// way to the screen, where it is an em dash.
/// </remarks>
public static class VanPerformanceCharts
{
    /// <summary>The currencies the period took, largest first — the order the summary keeps.</summary>
    public static IReadOnlyList<string> Currencies(VanSalesPerformanceReportResponse report) =>
        report.Summary.TotalsByCurrency.Select(total => total.Currency).ToList();

    public static string Money(string currency, decimal amount) =>
        $"{currency} {amount.ToString("N0", CultureInfo.InvariantCulture)}";

    public static string Money2(string currency, decimal amount) =>
        $"{currency} {amount.ToString("N2", CultureInfo.InvariantCulture)}";

    private static decimal? GrossIn(IEnumerable<VanSalesMoney> totals, string currency) =>
        totals.FirstOrDefault(total => total.Currency == currency)?.Gross;

    private static decimal? GrossIn(IEnumerable<VanSalesLineMoney> totals, string currency) =>
        totals.FirstOrDefault(total => total.Currency == currency)?.Gross;

    /// <summary>"80%". Written out rather than "P0", which puts a space before the sign in the invariant culture.</summary>
    private static string Percent(double fraction) =>
        Math.Round(fraction * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string RateText(double? rate) =>
        rate is { } value ? Percent(value) : "—";

    // ── Over time ────────────────────────────────────────────────────────────────

    /// <summary>Takings per day in one currency, with productive calls as the line.</summary>
    public static IReadOnlyList<VspColumn> Daily(VanSalesPerformanceReportResponse report, string currency)
    {
        var days = report.Trend.Daily;

        // Every day is drawn; only some are labelled, so a quarter still reads.
        var every = Math.Max(1, (int)Math.Ceiling(days.Count / 16.0));

        return days.Select((day, index) =>
        {
            var gross = GrossIn(day.TotalsByCurrency, currency);
            var traded = day.DocumentCount > 0;
            var date = day.TradingDate.ToString("ddd dd MMM", CultureInfo.InvariantCulture);
            var tip = !traded
                ? $"{date} · nothing sold"
                : gross is { } value
                    ? $"{date} · {Money(currency, value)} · {day.ProductiveCalls:N0} productive calls"
                    : $"{date} · no {currency} sales · {day.ProductiveCalls:N0} productive calls";

            return new VspColumn(day.TradingDate.Day.ToString(CultureInfo.InvariantCulture), gross,
                gross is { } shown ? Money(currency, shown) : "—", tip)
            {
                Line = traded ? day.ProductiveCalls : null,
                ShowLabel = index % every == 0,
                StrongLabel = day.DayOfWeek == System.DayOfWeek.Monday
            };
        }).ToList();
    }

    /// <summary>
    /// Takings per calendar day of each weekday. Divided by the days the period contained, not the
    /// days that traded, so a weekday nobody works reads as nobody working.
    /// </summary>
    public static IReadOnlyList<VspColumn> Weekdays(VanSalesPerformanceReportResponse report, string currency)
    {
        var points = report.Trend.DayOfWeek
            .Select(point => (Point: point, PerDay: point.CalendarDayCount > 0
                ? (GrossIn(point.TotalsByCurrency, currency) ?? 0m) / point.CalendarDayCount
                : (decimal?)null))
            .ToList();

        var busiest = points.Where(p => p.PerDay is > 0).Select(p => p.PerDay).DefaultIfEmpty(null).Max();

        return points.Select(p => new VspColumn(
            p.Point.Label.Length > 3 ? p.Point.Label[..3] : p.Point.Label,
            p.PerDay,
            p.PerDay is { } perDay ? perDay.ToString("N0", CultureInfo.InvariantCulture) : "—",
            p.PerDay is { } value
                ? $"{p.Point.Label} · {Money(currency, value)} per calendar day over {p.Point.CalendarDayCount:N0}"
                : $"{p.Point.Label} · not in the period")
        {
            Emphasis = p.PerDay is { } v && v == busiest
        }).ToList();
    }

    // ── Who and where ────────────────────────────────────────────────────────────

    /// <summary>Routes ranked on takings in one currency, with call compliance and productive call rate.</summary>
    public static IReadOnlyList<VspBarRow> Routes(VanSalesPerformanceReportResponse report, string currency) =>
        report.Routes
            .Select(route => (Route: route, Gross: GrossIn(route.TotalsByCurrency, currency)))
            .Where(r => r.Gross is not null)
            .OrderBy(r => r.Route.HasRouteDay ? 0 : 1)
            .ThenByDescending(r => r.Gross)
            .Select(r => new VspBarRow(r.Route.DisplayRoute, r.Gross!.Value, Money(currency, r.Gross.Value))
            {
                Sub = r.Route.HasRouteDay ? r.Route.Territory ?? "Territory not set" : "cannot be attributed",
                Muted = !r.Route.HasRouteDay,
                Extras =
                [
                    Rate(r.Route.CallComplianceRate),
                    Rate(r.Route.ProductiveCallRate)
                ]
            })
            .ToList();

    /// <summary>Reps ranked on takings in one currency, with their strike rate as a meter.</summary>
    public static IReadOnlyList<VspBarRow> Reps(VanSalesPerformanceReportResponse report, string currency) =>
        report.Reps
            .Select(rep => (Rep: rep, Gross: GrossIn(rep.TotalsByCurrency, currency)))
            .Where(r => r.Gross is not null)
            .OrderByDescending(r => r.Gross)
            .Select(r => new VspBarRow(r.Rep.DisplayName, r.Gross!.Value, Money(currency, r.Gross.Value))
            {
                Sub = RepSub(r.Rep),
                Meter = r.Rep.StrikeRate,
                Extras = [Rate(r.Rep.StrikeRate)]
            })
            .ToList();

    private static string RepSub(VanSalesRepRow rep)
    {
        var days = $"{rep.TradingDayCount:N0} day{(rep.TradingDayCount == 1 ? "" : "s")}";
        return rep.Routes.Count == 0 ? days : $"{string.Join(", ", rep.Routes)} · {days}";
    }

    /// <summary>
    /// A rate as a figure. Below three quarters reads as low; none reads as none — never as low.
    /// </summary>
    private static VspFigure Rate(double? rate) =>
        new(RateText(rate), None: rate is null, Low: rate is < 0.75);

    /// <summary>The items that took the most in one currency, with their share of that currency's line takings.</summary>
    public static (IReadOnlyList<VspBarRow> Rows, string? Remainder) Items(
        VanSalesPerformanceReportResponse report, string currency, int take = 8)
    {
        var priced = report.Items
            .Select(item => (Item: item, Gross: GrossIn(item.TotalsByCurrency, currency)))
            .Where(i => i.Gross is > 0)
            .OrderByDescending(i => i.Gross)
            .ToList();

        var total = priced.Sum(i => i.Gross!.Value);
        if (total <= 0)
        {
            return ([], null);
        }

        var rows = priced.Take(take).Select(i => new VspBarRow(i.Item.DisplayName, i.Gross!.Value, Money(currency, i.Gross.Value))
        {
            Sub = i.Item.ItemCode,
            Extras = [new VspFigure(Share(i.Gross.Value, total))]
        }).ToList();

        var rest = priced.Skip(take).ToList();
        string? remainder = rest.Count == 0
            ? null
            : $"{rest.Count:N0} other item{(rest.Count == 1 ? "" : "s")} make up the remaining {Share(rest.Sum(i => i.Gross!.Value), total)}.";

        return (rows, remainder);
    }

    private static string Share(decimal part, decimal whole) =>
        Percent((double)(part / whole));

    // ── Drops and prices ─────────────────────────────────────────────────────────

    /// <summary>How many drops fell in each band. The bands holding the middle half are emphasised.</summary>
    public static IReadOnlyList<VspColumn> Drops(VanSalesDropSize distribution) =>
        distribution.Buckets.Select(bucket => new VspColumn(
            bucket.Label,
            bucket.DropCount,
            bucket.DropCount.ToString("N0", CultureInfo.InvariantCulture),
            $"{bucket.Label} {distribution.Currency} · {bucket.DropCount:N0} drops"
                + (bucket.SharePercent is { } share ? $" · {share:N1}% of value" : ""))
        {
            Emphasis = Overlaps(bucket, distribution.P25, distribution.P75)
        }).ToList();

    private static bool Overlaps(VanSalesDropSizeBucket bucket, decimal low, decimal high) =>
        bucket.LowerBound <= high && (bucket.UpperBound is not { } upper || upper > low);

    /// <summary>
    /// Each item's lowest and highest unit price, placed against its own weighted average. Peer-relative:
    /// there is no trustworthy list price to place them against instead.
    /// </summary>
    public static IReadOnlyList<VspPriceRange> Prices(
        VanSalesPerformanceReportResponse report, string currency, int take = 6)
    {
        // ±12% around the average fills the track; anything wider is pinned to the edge.
        const double Reach = 0.24;

        return report.ItemPrices
            .Where(price => price.Currency == currency && price.WeightedAveragePrice > 0)
            .OrderByDescending(price => price.Gross)
            .Take(take)
            .Select(price =>
            {
                var average = (double)price.WeightedAveragePrice;
                double Place(decimal value) =>
                    Math.Clamp(50 + ((double)value - average) / average / Reach * 100, 0, 100);

                var cheapest = price.Reps.FirstOrDefault(rep => rep.VarianceFromItemPercent is < 0);
                return new VspPriceRange(
                    price.DisplayName,
                    price.UoMCode is { Length: > 0 } unit ? $"{price.ItemCode} · per {unit}" : price.ItemCode,
                    Place(price.MinUnitPrice),
                    Place(price.MaxUnitPrice),
                    Money2(currency, price.MinUnitPrice),
                    Money2(currency, price.WeightedAveragePrice),
                    Money2(currency, price.MaxUnitPrice),
                    price.PriceSpreadPercent is { } spread ? $"{spread:N0}% spread" : "—",
                    price.PriceSpreadPercent is > 12m,
                    cheapest is null
                        ? null
                        : $"Lowest: {cheapest.DisplayName}, {Math.Abs(cheapest.VarianceFromItemPercent!.Value):N1}% under");
            })
            .ToList();
    }

    // ── Axis ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A round top for an axis drawn in quarters: 2,420 becomes 3,000 (ticks of 750), 61 becomes 80.
    /// Every candidate divides into four labels that read as round numbers.
    /// </summary>
    public static decimal NiceMax(decimal value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10((double)value)));
        var top = new[] { 1, 1.2, 1.6, 2, 3, 4, 6, 8, 10 }
            .Select(m => m * magnitude)
            .First(candidate => candidate >= (double)value);
        return (decimal)top;
    }
}
