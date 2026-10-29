using ShopInventory.Web.Components.VanPerformance;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The rows behind the van sales performance page's charts. A chart that looks right can still be
/// drawing a sum of two currencies, a silent day as a zero, or an unmeasured rate as 0% — none of
/// which a screenshot shows, so they are pinned here.
/// </summary>
public class VanPerformanceChartsTests
{
    [Fact]
    public void A_silent_day_has_no_value_and_breaks_the_calls_line()
    {
        var days = VanPerformanceCharts.Daily(Report(), "USD");

        Assert.Equal(3, days.Count);
        Assert.Equal(610m, days[0].Value);
        Assert.Equal(6, days[0].Line);

        Assert.Null(days[1].Value);
        Assert.Null(days[1].Line);
        Assert.Contains("nothing sold", days[1].Tip);
    }

    [Fact]
    public void A_day_that_sold_only_in_another_currency_is_not_drawn_as_a_sale()
    {
        var days = VanPerformanceCharts.Daily(Report(), "USD");

        // 5 Aug sold ZWG alone: it traded, so its calls are plotted, but it has nothing in USD.
        Assert.Null(days[2].Value);
        Assert.Equal(2, days[2].Line);
        Assert.Contains("no USD sales", days[2].Tip);

        var zwg = VanPerformanceCharts.Daily(Report(), "ZWG");
        Assert.Equal(900m, zwg[2].Value);
        Assert.Null(zwg[0].Value);
    }

    [Fact]
    public void Routes_rank_on_the_chosen_currency_with_unattributed_sales_last_and_muted()
    {
        var routes = VanPerformanceCharts.Routes(Report(), "USD");

        Assert.Equal(["Guruve", "Mvurwi", "No departure record"], routes.Select(route => route.Label).ToArray());
        Assert.True(routes[^1].Muted);
        Assert.False(routes[0].Muted);
    }

    [Fact]
    public void A_route_with_no_plan_shows_an_em_dash_flagged_as_none_not_low()
    {
        var mvurwi = VanPerformanceCharts.Routes(Report(), "USD").Single(route => route.Label == "Mvurwi");

        Assert.Equal("—", mvurwi.Extras[0].Text);
        Assert.True(mvurwi.Extras[0].None);
        Assert.False(mvurwi.Extras[0].Low);
    }

    [Fact]
    public void Rows_with_nothing_in_the_currency_are_left_out_rather_than_drawn_as_zero()
    {
        var zwgRoutes = VanPerformanceCharts.Routes(Report(), "ZWG");
        Assert.Empty(zwgRoutes);

        var zwgReps = VanPerformanceCharts.Reps(Report(), "ZWG");
        Assert.Single(zwgReps);
        Assert.Equal(900m, zwgReps[0].Value);
    }

    [Fact]
    public void A_rep_with_no_recorded_calls_has_no_meter_and_an_em_dash()
    {
        var reps = VanPerformanceCharts.Reps(Report(), "USD");

        Assert.Equal(["Tinashe Moyo", "Rudo Chikanga"], reps.Select(rep => rep.Label).ToArray());
        Assert.Equal(0.8, reps[0].Meter!.Value, 3);
        Assert.Null(reps[1].Meter);
        Assert.True(reps[1].Extras[0].None);
    }

    [Fact]
    public void Item_shares_are_of_the_one_currency_and_the_rest_is_summed_in_a_note()
    {
        var (rows, remainder) = VanPerformanceCharts.Items(Report(), "USD", take: 1);

        Assert.Single(rows);
        Assert.Equal("Cheddar 1kg", rows[0].Label);
        Assert.Equal("80%", rows[0].Extras[0].Text);
        Assert.Equal("1 other item make up the remaining 20%.", remainder);
    }

    [Fact]
    public void A_weekday_averages_over_the_days_the_period_contained()
    {
        var weekdays = VanPerformanceCharts.Weekdays(Report(), "USD");

        Assert.Equal(400m, weekdays[0].Value);   // 2,000 over five Mondays, though only four traded
        Assert.True(weekdays[0].Emphasis);
        Assert.Equal(0m, weekdays[1].Value);     // Tuesdays traded nothing: a real zero
    }

    [Fact]
    public void The_drop_bands_holding_the_middle_half_are_emphasised()
    {
        var bands = VanPerformanceCharts.Drops(Report().DropSizes[0]);

        Assert.Equal([false, true, true, false], bands.Select(band => band.Emphasis).ToArray());
    }

    [Theory]
    [InlineData(2420, 3000)]
    [InlineData(80, 80)]
    [InlineData(61, 80)]
    [InlineData(0, 1)]
    public void An_axis_tops_out_at_a_round_number(int value, int expected) =>
        Assert.Equal(expected, VanPerformanceCharts.NiceMax(value));

    // --- Fixture ---

    private static VanSalesPerformanceReportResponse Report() => new()
    {
        FromDate = new DateTime(2026, 8, 1),
        ToDate = new DateTime(2026, 8, 31),
        Summary = new VanSalesPerformanceSummary
        {
            DocumentCount = 10,
            TotalsByCurrency = [Money("USD", 9, 8, 3000m), Money("ZWG", 1, 1, 900m)]
        },
        Routes =
        [
            Route("MVURWI", "Mvurwi", true, 300m, planned: null),
            new VanSalesRouteRow { HasRouteDay = false, TotalsByCurrency = [Money("USD", 1, 1, 900m)] },
            Route("GURUVE", "Guruve", true, 1800m, planned: 60)
        ],
        Reps =
        [
            new VanSalesRepRow
            {
                Username = "van011", FullName = "Rudo Chikanga", TradingDayCount = 3,
                TotalsByCurrency = [Money("USD", 2, 2, 400m), Money("ZWG", 1, 1, 900m)]
            },
            new VanSalesRepRow
            {
                Username = "van010", FullName = "Tinashe Moyo", TradingDayCount = 12,
                PcrCalls = 50, PcrProductiveCalls = 40,
                TotalsByCurrency = [Money("USD", 7, 6, 2600m)]
            }
        ],
        Items =
        [
            Item("CHE011", "Cheddar 1kg", 2400m),
            Item("PIC003", "Pickles 500g", 600m)
        ],
        Trend = new VanSalesTrend
        {
            Daily =
            [
                Day(3, DayOfWeek.Monday, 7, 6, [Money("USD", 7, 6, 610m)]),
                Day(4, DayOfWeek.Tuesday, 0, 0, []),
                Day(5, DayOfWeek.Wednesday, 1, 2, [Money("ZWG", 1, 1, 900m)])
            ],
            DayOfWeek =
            [
                new VanSalesSeasonPoint { Label = "Monday", CalendarDayCount = 5, ActiveDayCount = 4, TotalsByCurrency = [Money("USD", 20, 18, 2000m)] },
                new VanSalesSeasonPoint { Label = "Tuesday", CalendarDayCount = 4, ActiveDayCount = 0, TotalsByCurrency = [] }
            ]
        },
        DropSizes =
        [
            new VanSalesDropSize
            {
                Currency = "USD", DropCount = 30, P25 = 12m, Median = 30m, P75 = 60m, Mean = 40m,
                Buckets =
                [
                    new VanSalesDropSizeBucket { Label = "0–10", LowerBound = 0m, UpperBound = 10m, DropCount = 5 },
                    new VanSalesDropSizeBucket { Label = "10–25", LowerBound = 10m, UpperBound = 25m, DropCount = 9 },
                    new VanSalesDropSizeBucket { Label = "25–50", LowerBound = 25m, UpperBound = 50m, DropCount = 10 },
                    new VanSalesDropSizeBucket { Label = "100+", LowerBound = 100m, UpperBound = null, DropCount = 6 }
                ]
            }
        ]
    };

    private static VanSalesRouteRow Route(string code, string name, bool routed, decimal usd, int? planned) => new()
    {
        HasRouteDay = routed,
        RouteCode = code,
        RouteName = name,
        Territory = "Mashonaland Central",
        PlannedCalls = planned,
        Calls = planned is null ? null : 55,
        TotalsByCurrency = [Money("USD", 3, 3, usd)]
    };

    private static VanSalesItemRow Item(string code, string name, decimal usd) => new()
    {
        ItemCode = code,
        ItemDescription = name,
        TotalsByCurrency = [new VanSalesLineMoney { Currency = "USD", LineCount = 4, Gross = usd }]
    };

    private static VanSalesTrendPoint Day(int day, DayOfWeek weekday, int documents, int calls, List<VanSalesMoney> totals) => new()
    {
        TradingDate = new DateTime(2026, 8, day),
        DayOfWeek = weekday,
        DocumentCount = documents,
        ProductiveCalls = calls,
        TotalsByCurrency = totals
    };

    private static VanSalesMoney Money(string currency, int documents, int drops, decimal gross) =>
        new() { Currency = currency, DocumentCount = documents, DropCount = drops, Gross = gross };
}
