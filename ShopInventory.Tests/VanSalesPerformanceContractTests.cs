using System.Text.Json;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Serialises the API's result and reads it back as the portal's hand-mirrored DTO, exactly as the
/// wire does.
/// </summary>
/// <remarks>
/// This guards the failure this codebase produces most often and recognises least easily. The two
/// halves of every report in this app are written by hand in separate projects, and nothing makes
/// them agree. A property declared non-nullable on the portal side against a value the API can send
/// as null makes System.Text.Json throw inside <c>GetFromJsonAsync</c>; the service's catch turns
/// that into a null return, and the page renders "no data" — which reads as a quiet month rather
/// than as a broken contract. It cost two months on the customer portal statements.
///
/// Both sides use <see cref="JsonSerializerDefaults.Web"/> because that is what ASP.NET Core writes
/// with and what <c>HttpClient.GetFromJsonAsync</c> reads with. Testing against anything else would
/// pass while production failed.
/// </remarks>
public class VanSalesPerformanceContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static VanSalesPerformanceReportResponse RoundTrip(VanSalesPerformanceReportResult result)
    {
        var json = JsonSerializer.Serialize(result, Wire);
        var mirrored = JsonSerializer.Deserialize<VanSalesPerformanceReportResponse>(json, Wire);

        Assert.NotNull(mirrored);
        return mirrored;
    }

    /// <summary>
    /// A fully populated report has to survive the trip with every figure in the right place. A
    /// property the mirror spells differently does not throw — it silently arrives as zero.
    /// </summary>
    [Fact]
    public void A_populated_report_crosses_the_wire_intact()
    {
        var mirrored = RoundTrip(Populated());

        Assert.Equal(new DateTime(2026, 8, 1), mirrored.FromDate);
        Assert.Equal(40, mirrored.Summary.DocumentCount);
        Assert.Equal(44, mirrored.Summary.ProductiveCalls);
        Assert.Equal(4200m, Assert.Single(mirrored.Summary.TotalsByCurrency).Gross);

        var route = Assert.Single(mirrored.Routes);
        Assert.Equal("GURUVE", route.RouteCode);
        Assert.Equal("Mashonaland Central", route.Territory);
        Assert.Equal(820, route.KilometresTravelled);

        var rep = Assert.Single(mirrored.Reps);
        Assert.Equal("Tinashe Moyo", rep.FullName);
        Assert.Equal(["GURUVE"], rep.Routes);
        Assert.Equal(3, rep.NewOutlets);
        Assert.Equal(2, rep.NewOutletsWhoBought);

        var item = Assert.Single(mirrored.Items);
        Assert.Equal("CHE011", item.ItemCode);
        Assert.Equal(260m, Assert.Single(item.QuantitiesByUoM).Quantity);

        var price = Assert.Single(mirrored.ItemPrices);
        Assert.Equal(10m, price.WeightedAveragePrice);
        Assert.Equal(-20m, Assert.Single(price.Reps).VarianceFromItemPercent);

        var drop = Assert.Single(mirrored.DropSizes);
        Assert.Equal(95m, drop.Median);
        Assert.Equal(2, drop.Buckets.Count);

        Assert.Equal(2, mirrored.Trend.Daily.Count);
        Assert.Equal(DayOfWeek.Monday, mirrored.Trend.Daily[0].DayOfWeek);
    }

    /// <summary>
    /// The case that actually breaks these mirrors. Every nullable the API can send as null is sent
    /// as null here at once — a route nobody opened a day for, a rate with no denominator, an
    /// odometer never read, a unit of measure the handset does not send.
    ///
    /// If any of them is non-nullable on the portal side this throws, and the failure is the point:
    /// in production it would be swallowed and shown as an empty page.
    /// </summary>
    [Fact]
    public void Every_null_the_api_can_send_survives_the_mirror()
    {
        var mirrored = RoundTrip(AllNulls());

        Assert.Null(mirrored.Summary.Calls);
        Assert.Null(mirrored.Summary.KilometresTravelled);
        Assert.Null(mirrored.Summary.StrikeRate);

        var route = Assert.Single(mirrored.Routes);
        Assert.False(route.HasRouteDay);
        Assert.Null(route.RouteCode);
        Assert.Null(route.RouteName);
        Assert.Null(route.Territory);
        Assert.Null(route.PlannedCalls);
        Assert.Null(route.Calls);
        Assert.Null(route.KilometresTravelled);
        Assert.Null(route.CallComplianceRate);
        Assert.Null(route.ProductiveCallRate);

        var rep = Assert.Single(mirrored.Reps);
        Assert.Null(rep.FullName);
        Assert.Null(rep.Calls);
        Assert.Null(rep.OutletsVisited);
        Assert.Null(rep.StrikeRate);
        Assert.Null(rep.CallsPerDay);

        var item = Assert.Single(mirrored.Items);
        Assert.Null(item.ItemDescription);
        Assert.Null(Assert.Single(item.QuantitiesByUoM).UoMCode);

        var price = Assert.Single(mirrored.ItemPrices);
        Assert.Null(price.ItemDescription);
        Assert.Null(price.UoMCode);
        // A zero average has no meaningful spread, and must read as unavailable rather than as 0%.
        Assert.Null(price.PriceSpreadPercent);
        Assert.Null(Assert.Single(price.Reps).VarianceFromItemPercent);

        var territory = Assert.Single(mirrored.Territories);
        Assert.Null(territory.Territory);
        Assert.Equal("Territory not set", territory.DisplayTerritory);

        var season = Assert.Single(mirrored.Trend.Monthly);
        Assert.Null(season.Year);
        Assert.Null(season.Month);
        Assert.Null(season.DayOfWeek);

        var bucket = Assert.Single(Assert.Single(mirrored.DropSizes).Buckets);
        Assert.Null(bucket.UpperBound);
        Assert.Null(bucket.SharePercent);
    }

    /// <summary>
    /// The empty period, which is what a first run against a quiet window looks like. Every list has
    /// to arrive as an empty list rather than as null, or the page throws on the first foreach.
    /// </summary>
    [Fact]
    public void An_empty_report_arrives_with_empty_lists_rather_than_nulls()
    {
        var mirrored = RoundTrip(new VanSalesPerformanceReportResult(
            FromDate: new DateTime(2026, 8, 1),
            ToDate: new DateTime(2026, 8, 31),
            Summary: new VanSalesPerformanceSummaryResult(0, 0, 0, 0, 0, null, 0, 0, 0, 0, null, []),
            Territories: [],
            Routes: [],
            Reps: [],
            Items: [],
            LapsedItems: [],
            Trend: new VanSalesTrendResult([], [], []),
            ItemPrices: [],
            DropSizes: [],
            Coverage: new VanSalesCoverageResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)));

        Assert.Empty(mirrored.Routes);
        Assert.Empty(mirrored.Reps);
        Assert.Empty(mirrored.Items);
        Assert.Empty(mirrored.Trend.Daily);
        Assert.Empty(mirrored.Summary.TotalsByCurrency);
        Assert.True(mirrored.Coverage.IsClean);
        Assert.Empty(mirrored.Coverage.Caveats);
    }

    /// <summary>
    /// The coverage block's own rendering. Both sides compute <c>IsClean</c> independently, so this
    /// pins that they agree — a page hiding its caveats strip because the mirror disagreed about
    /// what "clean" means would hide exactly the thing the strip exists to show.
    /// </summary>
    [Fact]
    public void Coverage_is_read_as_dirty_on_both_sides_when_anything_is_missing()
    {
        var result = new VanSalesPerformanceReportResult(
            FromDate: new DateTime(2026, 8, 1),
            ToDate: new DateTime(2026, 8, 31),
            Summary: new VanSalesPerformanceSummaryResult(0, 0, 0, 0, 0, null, 0, 0, 0, 0, null, []),
            Territories: [],
            Routes: [],
            Reps: [],
            Items: [],
            LapsedItems: [],
            Trend: new VanSalesTrendResult([], [], []),
            ItemPrices: [],
            DropSizes: [],
            Coverage: new VanSalesCoverageResult(
                SaleCount: 12,
                LineCount: 30,
                SalesWithoutRouteCustomer: 2,
                SalesWithoutRouteDay: 3,
                RepDaysWithoutRouteDay: 1,
                SalesWithAssumedCurrency: 0,
                SalesWithoutPaymentMethod: 4,
                LinesWithoutUoM: 30,
                LinesWithZeroQuantity: 0,
                RepsWithoutVisitData: 1,
                ReferencesInBothSources: 1));

        Assert.False(result.Coverage.IsClean);

        var mirrored = RoundTrip(result);

        Assert.False(mirrored.Coverage.IsClean);

        var caveats = mirrored.Coverage.Caveats.ToList();
        Assert.Equal(4, caveats.Count);

        // The double-count leads: it is an ingest fault rather than a limit of the report, and it is
        // the only one on the list that makes a figure too large instead of too small.
        Assert.Contains("counted twice", caveats[0]);
    }

    // --- Fixtures ---

    private static VanSalesPerformanceReportResult Populated() => new(
        FromDate: new DateTime(2026, 8, 1),
        ToDate: new DateTime(2026, 8, 31),
        Summary: new VanSalesPerformanceSummaryResult(
            RepCount: 2,
            RouteCount: 1,
            TerritoryCount: 1,
            TradingDayCount: 12,
            DocumentCount: 40,
            Calls: 55,
            ProductiveCalls: 44,
            CustomerCount: 30,
            ItemCount: 12,
            NewOutlets: 3,
            KilometresTravelled: 820,
            TotalsByCurrency: [new VanSalesMoneyResult("USD", 40, 33, 4200m)]),
        Territories:
        [
            new VanSalesTerritoryResult("Mashonaland Central", 1, 2, 12, 44, 30,
                [new VanSalesMoneyResult("USD", 40, 33, 4200m)])
        ],
        Routes:
        [
            new VanSalesRouteResult(true, "GURUVE", "Guruve", "Mashonaland Central", 2, 12,
                60, 55, 44, 30, 820, [new VanSalesMoneyResult("USD", 40, 33, 4200m)])
        ],
        Reps:
        [
            new VanSalesRepResult(Guid.NewGuid(), "van010", "Tinashe Moyo", ["GURUVE"],
                12, 55, 30, 44, 30, 3, 2, 12, 820,
                [new VanSalesMoneyResult("USD", 40, 33, 4200m)])
        ],
        Items:
        [
            new VanSalesItemResult(1, "CHE011", "Cheddar 1kg", 22, 20, 18, 2, 11,
                new DateTime(2026, 8, 2), new DateTime(2026, 8, 29),
                [new VanSalesQuantityResult(null, 260m, 22)],
                [new VanSalesLineMoneyResult("USD", 22, 2600m)])
        ],
        LapsedItems:
        [
            new VanSalesLapsedItemResult("PIC003", "Pickles 500g", new DateTime(2026, 7, 20), 42, 9, 7,
                [new VanSalesLineMoneyResult("USD", 9, 310m)])
        ],
        Trend: new VanSalesTrendResult(
            Daily:
            [
                new VanSalesTrendPointResult(new DateTime(2026, 8, 3), DayOfWeek.Monday, 2, 6, 7,
                    [new VanSalesMoneyResult("USD", 7, 6, 610m)]),
                new VanSalesTrendPointResult(new DateTime(2026, 8, 4), DayOfWeek.Tuesday, 0, 0, 0, [])
            ],
            DayOfWeek:
            [
                new VanSalesSeasonPointResult("Monday", null, null, DayOfWeek.Monday, false, 5, 4, 20, 18,
                    [new VanSalesMoneyResult("USD", 20, 18, 2100m)])
            ],
            Monthly:
            [
                new VanSalesSeasonPointResult("Aug 2026", 2026, 8, null, false, 31, 12, 40, 44,
                    [new VanSalesMoneyResult("USD", 40, 33, 4200m)])
            ]),
        ItemPrices:
        [
            new VanSalesItemPriceResult("CHE011", "Cheddar 1kg", "USD", null, 22, 260m, 2600m,
                10m, 8m, 11m,
                [
                    new VanSalesRepPriceResult(Guid.NewGuid(), "van011", "Rudo Chikanga",
                        6, 60m, 480m, 8m, -20m)
                ])
        ],
        DropSizes:
        [
            new VanSalesDropSizeResult("USD", 33, 4200m, 4m, 40m, 95m, 180m, 640m, 127.27m,
            [
                new VanSalesDropSizeBucketResult("0–5", 0m, 5m, 2, 8m) { SharePercent = 0.19 },
                new VanSalesDropSizeBucketResult("100+", 100m, null, 14, 3400m) { SharePercent = 80.95 }
            ])
        ],
        Coverage: new VanSalesCoverageResult(40, 96, 0, 0, 0, 0, 0, 96, 0, 0, 0));

    /// <summary>Every nullable the API can send as null, sent as null at once.</summary>
    private static VanSalesPerformanceReportResult AllNulls() => new(
        FromDate: new DateTime(2026, 8, 1),
        ToDate: new DateTime(2026, 8, 31),
        Summary: new VanSalesPerformanceSummaryResult(1, 0, 0, 1, 1, null, 0, 0, 1, 0, null, []),
        Territories: [new VanSalesTerritoryResult(null, 1, 1, 1, 0, 0, [])],
        Routes:
        [
            new VanSalesRouteResult(false, null, null, null, 1, 1, null, null, 0, 0, null, [])
        ],
        Reps:
        [
            new VanSalesRepResult(Guid.NewGuid(), "van010", null, [], 0, null, null, 0, 0, 0, 0, 0, null, [])
        ],
        Items:
        [
            new VanSalesItemResult(1, "CHE011", null, 1, 1, 0, 1, 1,
                new DateTime(2026, 8, 2), new DateTime(2026, 8, 2),
                [new VanSalesQuantityResult(null, 1m, 1)], [])
        ],
        LapsedItems: [],
        Trend: new VanSalesTrendResult(
            Daily: [],
            DayOfWeek: [],
            Monthly:
            [
                new VanSalesSeasonPointResult("Aug 2026", null, null, null, true, 1, 0, 0, 0, [])
            ]),
        ItemPrices:
        [
            new VanSalesItemPriceResult("CHE011", null, "USD", null, 1, 1m, 0m, 0m, 0m, 0m,
            [
                new VanSalesRepPriceResult(Guid.NewGuid(), "van010", null, 1, 1m, 0m, 0m, null)
            ])
        ],
        DropSizes:
        [
            new VanSalesDropSizeResult("USD", 1, 0m, 0m, 0m, 0m, 0m, 0m, 0m,
            [
                new VanSalesDropSizeBucketResult("0+", 0m, null, 1, 0m)
            ])
        ],
        Coverage: new VanSalesCoverageResult(1, 1, 1, 1, 1, 0, 0, 1, 0, 1, 0));
}
