using System.Text.Json;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesCoverageReport;
using ShopInventory.Web.Models;
using ApiGranularity = ShopInventory.Features.VanSalesReports.Queries.GetVanSalesCoverageReport.VanSalesCoverageGranularity;
using ApiGap = ShopInventory.Features.VanSalesReports.Queries.GetVanSalesCoverageReport.VanSalesCoverageGap;
using WebGranularity = ShopInventory.Web.Models.VanSalesCoverageGranularity;
using WebGap = ShopInventory.Web.Models.VanSalesCoverageGap;

namespace ShopInventory.Tests;

/// <summary>
/// Sends the coverage report across the wire and reads it back as the portal's hand-mirrored DTO.
/// </summary>
/// <remarks>
/// Same guard as the performance report's contract tests, and it matters more here: this report is
/// almost entirely made of nullables that mean "cannot say" rather than "none". A rep with no
/// account, a rate with no denominator, a shop that has never bought — if any of those is
/// non-nullable on the portal side, System.Text.Json throws inside GetFromJsonAsync, the service's
/// catch turns it into a null return, and the page renders "no data" as though the vans had a quiet
/// quarter.
///
/// The two enums are mirrored by hand as well, so they are checked by value rather than assumed.
/// </remarks>
public class VanSalesCoverageContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static VanSalesCoverageReportResponse RoundTrip(VanSalesCoverageReportResult result)
    {
        var json = JsonSerializer.Serialize(result, Wire);
        var mirrored = JsonSerializer.Deserialize<VanSalesCoverageReportResponse>(json, Wire);

        Assert.NotNull(mirrored);
        return mirrored;
    }

    [Fact]
    public void A_populated_report_crosses_the_wire_intact()
    {
        var mirrored = RoundTrip(Populated());

        Assert.Equal(90, mirrored.LapseDays);
        Assert.Equal(WebGranularity.Month, mirrored.Granularity);
        Assert.Equal(new DateTime(2026, 5, 3), mirrored.PriorWindowFrom);

        Assert.Equal(120, mirrored.Summary.RosterSize);
        Assert.Equal(0.8, mirrored.Summary.StrikeRate!.Value, 3);

        var rep = Assert.Single(mirrored.Reps);
        Assert.True(rep.OutletsAttributable);
        Assert.Equal("VAN010", rep.VanAccountCode);
        Assert.Equal(1200, Assert.Single(rep.EfficiencyByCurrency).Kilometres);

        var uncovered = Assert.Single(mirrored.UncoveredOutlets);
        Assert.Equal(WebGap.NotVisitedInWindow, uncovered.Gap);
        Assert.Equal("Not called on this period", uncovered.GapLabel);
        Assert.False(uncovered.HasNeverBought);

        var churn = Assert.Single(mirrored.Churn);
        Assert.Equal(0, churn.UnexplainedMovement);
        Assert.Equal(102, churn.ClosingActiveOutlets);

        var lapsed = Assert.Single(mirrored.LapsedOutlets);
        Assert.True(lapsed.StillOnRoster);
        Assert.Equal(100m, Assert.Single(lapsed.LastPurchaseByCurrency).Gross);

        var concentration = Assert.Single(mirrored.Concentration);
        Assert.Equal(50d, concentration.Top1SharePercent);
        Assert.Equal(2, concentration.TopOutlets.Count);

        Assert.Equal(0.5, Assert.Single(mirrored.Outlets).ConversionRate);
        Assert.Equal(0.75, mirrored.LocationIntegrity.GpsFixRate);
    }

    /// <summary>
    /// The case that breaks these mirrors. Every nullable the API can send as null, sent as null at
    /// once — and each one here means "cannot say", never zero.
    /// </summary>
    [Fact]
    public void Every_null_the_api_can_send_survives_the_mirror()
    {
        var mirrored = RoundTrip(AllNulls());

        Assert.Null(mirrored.Summary.RosterSize);
        Assert.Null(mirrored.Summary.Calls);
        Assert.Null(mirrored.Summary.PlannedCalls);
        Assert.Null(mirrored.Summary.KilometresTravelled);
        Assert.Null(mirrored.Summary.StrikeRate);
        Assert.Null(mirrored.Summary.CallComplianceRate);
        Assert.Null(mirrored.Summary.RosterCoverageRate);

        var trend = Assert.Single(mirrored.Trend);
        Assert.Null(trend.PlannedCalls);
        Assert.Null(trend.Calls);
        Assert.Null(trend.CallComplianceRate);
        Assert.Null(trend.ProductiveCallRate);

        var rep = Assert.Single(mirrored.Reps);
        Assert.False(rep.OutletsAttributable);
        Assert.Null(rep.FullName);
        Assert.Null(rep.VanAccountCode);
        Assert.Null(rep.RosterSize);
        Assert.Null(rep.Calls);
        Assert.Null(rep.OutletsVisited);
        Assert.Null(rep.OutletsBought);
        Assert.Null(rep.OutletsUncovered);
        Assert.Null(rep.KilometresTravelled);
        Assert.Null(rep.StrikeRate);
        Assert.Null(rep.RosterCoverageRate);

        var efficiency = Assert.Single(rep.EfficiencyByCurrency);
        Assert.Null(efficiency.Kilometres);
        Assert.Null(efficiency.GrossPerKilometre);
        Assert.Null(efficiency.KilometresPerDrop);

        var uncovered = Assert.Single(mirrored.UncoveredOutlets);
        Assert.Null(uncovered.OutletName);
        Assert.Null(uncovered.Phone);
        Assert.Null(uncovered.LastVisitedOn);
        Assert.Null(uncovered.LastPurchaseOn);
        Assert.Null(uncovered.DaysSinceLastPurchase);
        Assert.True(uncovered.HasNeverBought);
        Assert.Equal(WebGap.NeverVisited, uncovered.Gap);

        var churn = Assert.Single(mirrored.Churn);
        Assert.Null(churn.ChurnRate);
        Assert.Null(churn.GrowthRate);

        var lapsed = Assert.Single(mirrored.LapsedOutlets);
        Assert.Null(lapsed.OutletName);
        Assert.Null(lapsed.LastVisitedOn);
        Assert.Null(lapsed.LastSoldByRep);
        Assert.Null(lapsed.LastRouteCode);

        var concentration = Assert.Single(mirrored.Concentration);
        Assert.Null(concentration.RouteCode);
        Assert.Null(concentration.RouteName);
        Assert.Null(concentration.OutletsForHalfOfGross);
        Assert.Null(concentration.Top1SharePercent);
        Assert.Null(concentration.UnattributedSharePercent);
        Assert.Equal("No departure record", concentration.DisplayRoute);

        var outlet = Assert.Single(mirrored.Outlets);
        Assert.Null(outlet.VisitCount);
        Assert.Null(outlet.FirstEverPurchaseOn);
        Assert.Null(outlet.AverageDaysBetweenPurchases);
        Assert.Null(outlet.ConversionRate);

        Assert.Null(mirrored.LocationIntegrity.GpsFixRate);
        Assert.Null(mirrored.Quality.EarliestObservedSale);
    }

    /// <summary>An empty period must arrive with empty lists, or the page throws on its first loop.</summary>
    [Fact]
    public void An_empty_report_arrives_with_empty_lists()
    {
        var mirrored = RoundTrip(Empty());

        Assert.Empty(mirrored.Trend);
        Assert.Empty(mirrored.Reps);
        Assert.Empty(mirrored.UncoveredOutlets);
        Assert.Empty(mirrored.Churn);
        Assert.Empty(mirrored.LapsedOutlets);
        Assert.Empty(mirrored.Concentration);
        Assert.Empty(mirrored.Outlets);
        Assert.Empty(mirrored.LocationIntegrity.Reps);
    }

    /// <summary>
    /// The roster caveat is always present, because the roster is always live. It is the one
    /// limitation a reader has to carry into every figure on the uncovered register.
    /// </summary>
    [Fact]
    public void The_live_roster_caveat_always_reaches_the_page()
    {
        var caveats = RoundTrip(Empty()).Quality.Caveats.ToList();

        Assert.Contains(caveats, caveat => caveat.Contains("today's roster"));
    }

    /// <summary>Both hand-mirrored enums have to line up by value, not just by name.</summary>
    [Theory]
    [InlineData(ApiGap.NeverVisited, WebGap.NeverVisited)]
    [InlineData(ApiGap.NotVisitedInWindow, WebGap.NotVisitedInWindow)]
    [InlineData(ApiGap.VisitedNotBought, WebGap.VisitedNotBought)]
    public void The_gap_enum_is_mirrored_by_value(ApiGap api, WebGap web) =>
        Assert.Equal((int)api, (int)web);

    [Theory]
    [InlineData(ApiGranularity.Week, WebGranularity.Week)]
    [InlineData(ApiGranularity.Month, WebGranularity.Month)]
    public void The_granularity_enum_is_mirrored_by_value(ApiGranularity api, WebGranularity web) =>
        Assert.Equal((int)api, (int)web);

    // --- Fixtures ---

    private static VanSalesCoverageReportResult Empty() => new(
        FromDate: new DateTime(2026, 8, 1),
        ToDate: new DateTime(2026, 8, 31),
        PriorWindowFrom: new DateTime(2026, 5, 3),
        LapseDays: 90,
        Granularity: ApiGranularity.Month,
        Summary: new VanSalesCoverageSummaryResult(0, null, 0, 0, 0, 0, 0, 0, 0, 0, null, 0, null, null, []),
        Trend: [],
        Reps: [],
        UncoveredOutlets: [],
        LocationIntegrity: new VanSalesLocationIntegrityResult(0, 0, 0, 0, 0, 0, 0, 250, []),
        Churn: [],
        LapsedOutlets: [],
        Concentration: [],
        Outlets: [],
        Quality: new VanSalesCoverageQualityResult(0, 0, 0, 0, 0, 0, 0, 0, true, null));

    private static VanSalesCoverageReportResult Populated() => new(
        FromDate: new DateTime(2026, 8, 1),
        ToDate: new DateTime(2026, 8, 31),
        PriorWindowFrom: new DateTime(2026, 5, 3),
        LapseDays: 90,
        Granularity: ApiGranularity.Month,
        Summary: new VanSalesCoverageSummaryResult(
            RepCount: 1,
            RosterSize: 120,
            OutletsVisited: 95,
            OutletsVisitedOnRoster: 92,
            OutletsVisitedOffRoster: 3,
            OutletsBought: 80,
            OutletsUncovered: 25,
            NewOutlets: 6,
            ReactivatedOutlets: 3,
            LapsedOutlets: 4,
            Calls: 100,
            ProductiveCalls: 80,
            PlannedCalls: 120,
            KilometresTravelled: 1200,
            TotalsByCurrency: [new VanSalesMoneyResult("USD", 260, 210, 8400m)]),
        Trend:
        [
            new VanSalesCoverageTrendPointResult("Aug 2026", new DateTime(2026, 8, 1),
                new DateTime(2026, 8, 31), false, 1, 120, 100, 80, 80, 0, 0)
        ],
        Reps:
        [
            new VanSalesRepCoverageResult(
                Guid.NewGuid(), "van010", "Tinashe Moyo", "VAN010", ["GURUVE"],
                true, 120, 22, 100, 95, 92, 3, 80, 80, 25, 120, 1200,
                [new VanSalesEfficiencyResult("USD", 8400m, 210, 1200, 20, 2)],
                [new VanSalesMoneyResult("USD", 260, 210, 8400m)])
        ],
        UncoveredOutlets:
        [
            new VanSalesUncoveredOutletResult("VAN010", "TUCK01", "Tuck Shop", "0772 000 000",
                ApiGap.NotVisitedInWindow, new DateTime(2026, 6, 12), new DateTime(2026, 6, 12), 80,
                new DateTime(2026, 1, 5), ["Tinashe Moyo"])
        ],
        LocationIntegrity: new VanSalesLocationIntegrityResult(100, 75, 15, 10, 5, 8, 12, 250,
        [
            new VanSalesRepLocationResult(Guid.NewGuid(), "van010", "Tinashe Moyo", 100, 75, 15, 10, 8, 12)
        ]),
        Churn:
        [
            new VanSalesChurnPointResult("Aug 2026", new DateTime(2026, 8, 1), new DateTime(2026, 8, 31),
                false, false, 97, 80, 6, 3, 4, 102,
                [new VanSalesMoneyResult("USD", 260, 210, 8400m)],
                [new VanSalesMoneyResult("USD", 12, 10, 340m)])
        ],
        LapsedOutlets:
        [
            new VanSalesLapsedOutletResult("VAN010", "GONE1", "Used To Buy", "0772 111 111",
                new DateTime(2026, 5, 2), 121, new DateTime(2026, 5, 2), "Tinashe Moyo", "GURUVE",
                true, 14,
                [new VanSalesMoneyResult("USD", 2, 1, 100m)],
                [new VanSalesMoneyResult("USD", 20, 14, 900m)],
                ["Tinashe Moyo"])
        ],
        Concentration:
        [
            new VanSalesConcentrationResult(true, "GURUVE", "Guruve", "USD", 40, 8000m, 400m,
                4000m, 6000m, 7000m, 1,
                [
                    new VanSalesOutletShareResult(1, "BIG1", "Big Shop", 4000m, 50),
                    new VanSalesOutletShareResult(2, "BIG2", "Second Shop", 2000m, 25)
                ])
        ],
        Outlets:
        [
            new VanSalesOutletActivityResult("VAN010", "TUCK02", "Busy Shop", 8, 4, 5, 12, 3.5m,
                new DateTime(2026, 8, 3), new DateTime(2026, 8, 27), new DateTime(2025, 2, 1), 8.0,
                [new VanSalesMoneyResult("USD", 5, 4, 420m)])
        ],
        Quality: new VanSalesCoverageQualityResult(260, 0, 0, 0, 0, 0, 0, 0, true,
            new DateTime(2025, 2, 1)));

    private static VanSalesCoverageReportResult AllNulls() => new(
        FromDate: new DateTime(2026, 8, 1),
        ToDate: new DateTime(2026, 8, 31),
        PriorWindowFrom: new DateTime(2026, 5, 3),
        LapseDays: 90,
        Granularity: ApiGranularity.Week,
        Summary: new VanSalesCoverageSummaryResult(1, null, 0, 0, 0, 0, 0, 0, 0, 0, null, 0, null, null, []),
        Trend:
        [
            new VanSalesCoverageTrendPointResult("w/c 03 Aug", new DateTime(2026, 8, 3),
                new DateTime(2026, 8, 9), true, 0, null, null, 0, 0, 1, 1)
        ],
        Reps:
        [
            new VanSalesRepCoverageResult(
                Guid.NewGuid(), "van010", null, null, [],
                false, null, 0, null, null, null, null, 0, null, null, null, null,
                [new VanSalesEfficiencyResult("USD", 0m, 0, null, 0, 3)],
                [])
        ],
        UncoveredOutlets:
        [
            new VanSalesUncoveredOutletResult("VAN010", "NEW1", null, null,
                ApiGap.NeverVisited, null, null, null, new DateTime(2026, 7, 1), [])
        ],
        LocationIntegrity: new VanSalesLocationIntegrityResult(0, 0, 0, 0, 0, 0, 0, 250, []),
        Churn:
        [
            new VanSalesChurnPointResult("w/c 03 Aug", new DateTime(2026, 8, 3), new DateTime(2026, 8, 9),
                true, true, 0, 0, 0, 0, 0, 0, [], [])
        ],
        LapsedOutlets:
        [
            new VanSalesLapsedOutletResult("VAN010", "GONE1", null, null,
                new DateTime(2026, 5, 2), 121, null, null, null, false, 1, [], [], [])
        ],
        Concentration:
        [
            new VanSalesConcentrationResult(false, null, null, "USD", 0, 0m, 0m, 0m, 0m, 0m, null, [])
        ],
        Outlets:
        [
            new VanSalesOutletActivityResult("VAN010", "TUCK02", null, null, 1, 1, 0, 0m,
                new DateTime(2026, 8, 3), new DateTime(2026, 8, 3), null, null, [])
        ],
        Quality: new VanSalesCoverageQualityResult(0, 1, 1, 1, 0, 0, 0, 0, true, null));
}
