using System.Text.Json;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesScorecardReport;
using ShopInventory.Web.Models;

// Both projects declare these enums, which is exactly what the last two tests are about — so
// they are aliased rather than imported, and every use below has to say which side it means.
using ApiBand = ShopInventory.Features.VanSalesReports.Queries.GetVanSalesScorecardReport.VanSalesScorecardBand;
using ApiGrouping = ShopInventory.Features.VanSalesReports.Queries.GetVanSalesScorecardReport.VanSalesScorecardGrouping;
using WebBand = ShopInventory.Web.Models.VanSalesScorecardBand;
using WebGrouping = ShopInventory.Web.Models.VanSalesScorecardGrouping;

namespace ShopInventory.Tests;

/// <summary>
/// Sends the period scorecard across the wire and reads it back as the portal's hand-mirrored DTOs.
/// </summary>
/// <remarks>
/// Same guard as the other van reports: a property declared non-nullable on the portal side against
/// a value the API can send as null makes System.Text.Json throw inside <c>GetFromJsonAsync</c>, the
/// service's catch turns that into a null return, and the page renders "no data" — a total failure
/// wearing the clothes of a quiet period.
///
/// This report is the one where that is most likely, because almost every field on it is nullable
/// and every null means the same thing: a comparison that does not exist. A row that did not trade
/// last period has no movement. A currency taken for the first time has no percentage change. A rep
/// whose handset never synced has no rate and therefore no band. If any of those arrived as zero the
/// page would report a flat week, a collapse, or a failing rep — and in each case the wrong answer
/// is the confident one.
///
/// Two tests below are about the mirror rather than the wire. The band is computed independently in
/// both projects from the same inputs, and the caveats are two hand-written copies of the same prose.
/// Nothing makes either pair agree, and a reader cannot tell which copy is on the screen.
/// </remarks>
public class VanSalesScorecardContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly Guid RepId = Guid.Parse("3c7e1b90-55a1-4f38-9f0d-1a2b3c4d5e6f");

    private static T RoundTrip<TSource, T>(TSource result)
    {
        var mirrored = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result, Wire), Wire);
        Assert.NotNull(mirrored);
        return mirrored;
    }

    // ── The whole report ────────────────────────────────────────────────────────

    [Fact]
    public void A_populated_scorecard_crosses_the_wire_intact()
    {
        var mirrored = RoundTrip<VanSalesScorecardReportResult, VanSalesScorecardReportResponse>(
            Populated());

        Assert.Equal(new DateTime(2026, 8, 10), mirrored.FromDate);
        Assert.Equal(new DateTime(2026, 8, 16), mirrored.ToDate);
        Assert.Equal(new DateTime(2026, 8, 3), mirrored.PriorFromDate);
        Assert.Equal(new DateTime(2026, 8, 9), mirrored.PriorToDate);

        // The enum crosses as its name under the Web defaults, not as an integer.
        Assert.Equal(WebGrouping.Route, mirrored.Grouping);
        Assert.Equal(0.95, mirrored.CallComplianceTarget);
        Assert.Equal(0.75, mirrored.StrikeRateTarget);

        Assert.Equal(3, mirrored.Summary.RowCount);
        Assert.Equal(1, mirrored.Summary.GreenCount);
        Assert.Equal(1, mirrored.Summary.AmberCount);
        Assert.Equal(0, mirrored.Summary.RedCount);
        Assert.Equal(1, mirrored.Summary.UnratedCount);
        Assert.Equal(120, mirrored.Summary.Calls);
        Assert.Equal(100, mirrored.Summary.CallsAgainstPlan);
        Assert.Equal(140, mirrored.Summary.PlannedCalls);
        Assert.Equal(90, mirrored.Summary.ProductiveCalls);

        var row = mirrored.Rows.Single(candidate => candidate.Key == "GURUVE");
        Assert.Equal("Guruve", row.Label);
        Assert.Equal("Mash Central", row.SubLabel);
        Assert.Equal(RepId, row.UserId);
        Assert.Equal(40, row.Calls);
        Assert.Equal(36, row.CallsAgainstPlan);
        Assert.Equal(30, row.ProductiveCalls);
        Assert.Equal(410, row.Kilometres);

        var takings = Assert.Single(row.TakingsByCurrency);
        Assert.Equal("USD", takings.Currency);
        Assert.Equal(1234.50m, takings.Gross);
    }

    // ── The nulls ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every nullable the API can send, sent as null at once. Each one is a comparison or a
    /// measurement that does not exist, and each would read as a confident wrong answer at zero.
    /// </summary>
    [Fact]
    public void Every_null_the_api_can_send_survives_the_mirror()
    {
        var result = new VanSalesScorecardReportResult(
            FromDate: new DateTime(2026, 8, 10),
            ToDate: new DateTime(2026, 8, 16),
            PriorFromDate: new DateTime(2026, 8, 3),
            PriorToDate: new DateTime(2026, 8, 9),
            Grouping: ApiGrouping.Rep,
            CallComplianceTarget: 0.95,
            StrikeRateTarget: 0.75,
            Summary: new VanSalesScorecardSummaryResult(
                RowCount: 1, GreenCount: 0, AmberCount: 0, RedCount: 0, UnratedCount: 1,
                TradingDays: 1,
                Calls: null, CallsAgainstPlan: null, PlannedCalls: null,
                ProductiveCalls: 0, OutletsBought: 0, NewOutlets: 0, Kilometres: null,
                PriorCalls: null, PriorCallsAgainstPlan: null, PriorPlannedCalls: null,
                PriorProductiveCalls: 0, PriorOutletsBought: 0,
                TakingsByCurrency: []),
            Rows:
            [
                new VanSalesScorecardRowResult(
                    Key: "no-route",
                    Label: "No departure record",
                    SubLabel: null,
                    UserId: null,
                    CallComplianceTarget: 0.95,
                    StrikeRateTarget: 0.75,
                    TradingDays: 1,
                    Calls: null, CallsAgainstPlan: null, PlannedCalls: null,
                    ProductiveCalls: 0, OutletsBought: 0, NewOutlets: 0, Kilometres: null,
                    SalesWithoutTender: 0, SalesWithoutOutlet: 0,
                    TakingsByCurrency: [],
                    PriorCalls: null, PriorCallsAgainstPlan: null, PriorPlannedCalls: null,
                    PriorProductiveCalls: 0, PriorOutletsBought: 0,
                    PriorTakingsByCurrency: [])
            ],
            TakingsMovement:
            [
                new VanSalesScorecardMovementResult("ZWG", Gross: 400m, PriorGross: null)
            ],
            Quality: new VanSalesScorecardQualityResult(
                RowCount: 1, UnratedRows: 1, RowsWithNoPriorPeriod: 1, RowsWithNoPlan: 1,
                SalesWithoutTender: 0, SalesWithoutOutlet: 0, PriorPeriodEmpty: true));

        var mirrored = RoundTrip<VanSalesScorecardReportResult, VanSalesScorecardReportResponse>(result);

        Assert.Null(mirrored.Summary.Calls);
        Assert.Null(mirrored.Summary.CallsAgainstPlan);
        Assert.Null(mirrored.Summary.PlannedCalls);
        Assert.Null(mirrored.Summary.Kilometres);
        Assert.Null(mirrored.Summary.PriorCalls);
        Assert.Null(mirrored.Summary.PriorCallsAgainstPlan);
        Assert.Null(mirrored.Summary.PriorPlannedCalls);

        // The computed rates, which are the figures a reader actually looks at.
        Assert.Null(mirrored.Summary.StrikeRate);
        Assert.Null(mirrored.Summary.PriorStrikeRate);
        Assert.Null(mirrored.Summary.StrikeRateMovement);
        Assert.Null(mirrored.Summary.CallComplianceRate);
        Assert.Null(mirrored.Summary.CallComplianceMovement);

        var row = Assert.Single(mirrored.Rows);
        Assert.Null(row.SubLabel);
        Assert.Null(row.UserId);
        Assert.Null(row.Calls);
        Assert.Null(row.Kilometres);
        Assert.Null(row.StrikeRate);
        Assert.Null(row.StrikeRateMovement);
        Assert.Null(row.CallComplianceRate);

        // No rate at all is the absence of a band, never the worst one.
        Assert.Equal(WebBand.Unrated, row.Band);

        var movement = Assert.Single(mirrored.TakingsMovement);
        Assert.Equal(400m, movement.Gross);
        Assert.Null(movement.PriorGross);
        Assert.Null(movement.Movement);
        Assert.Null(movement.PercentChange);
    }

    [Fact]
    public void An_empty_report_arrives_with_empty_lists_and_null_rates()
    {
        var result = new VanSalesScorecardReportResult(
            FromDate: new DateTime(2026, 8, 10),
            ToDate: new DateTime(2026, 8, 16),
            PriorFromDate: new DateTime(2026, 8, 3),
            PriorToDate: new DateTime(2026, 8, 9),
            Grouping: ApiGrouping.Rep,
            CallComplianceTarget: 0.95,
            StrikeRateTarget: 0.75,
            Summary: new VanSalesScorecardSummaryResult(
                0, 0, 0, 0, 0, 0, null, null, null, 0, 0, 0, null, null, null, null, 0, 0, []),
            Rows: [],
            TakingsMovement: [],
            Quality: new VanSalesScorecardQualityResult(0, 0, 0, 0, 0, 0, PriorPeriodEmpty: true));

        var mirrored = RoundTrip<VanSalesScorecardReportResult, VanSalesScorecardReportResponse>(result);

        Assert.Empty(mirrored.Rows);
        Assert.Empty(mirrored.TakingsMovement);
        Assert.Empty(mirrored.Summary.TakingsByCurrency);
        Assert.Null(mirrored.Summary.StrikeRate);
        Assert.Null(mirrored.Summary.RatedShare);
        Assert.NotEmpty(mirrored.Quality.Caveats);
    }

    // ── The two hand-written copies ─────────────────────────────────────────────

    /// <summary>
    /// The band is computed on both sides from the same inputs, and nothing makes the two agree.
    /// Walked across the boundaries rather than sampled, because an off-by-one in either copy would
    /// show only on the rows sitting exactly on a target.
    /// </summary>
    [Theory]
    // At target on both.
    [InlineData(100, 95, 100, 100, ApiBand.Green)]
    // Strike a shade under, well inside ten points.
    [InlineData(100, 95, 100, 70, ApiBand.Amber)]
    // Strike far under.
    [InlineData(100, 95, 100, 60, ApiBand.Red)]
    // Compliance far under, strike fine.
    [InlineData(100, 80, 100, 100, ApiBand.Red)]
    // Exactly ten points under is not yet red — the boundary itself.
    [InlineData(100, 85, 100, 65, ApiBand.Amber)]
    public void The_band_is_computed_identically_on_both_sides(
        int plannedCalls,
        int callsAgainstPlan,
        int calls,
        int productiveCalls,
        ApiBand expected)
    {
        var row = new VanSalesScorecardRowResult(
            Key: "R", Label: "R", SubLabel: null, UserId: null,
            CallComplianceTarget: 0.95, StrikeRateTarget: 0.75,
            TradingDays: 5,
            Calls: calls, CallsAgainstPlan: callsAgainstPlan, PlannedCalls: plannedCalls,
            ProductiveCalls: productiveCalls, OutletsBought: 0, NewOutlets: 0, Kilometres: null,
            SalesWithoutTender: 0, SalesWithoutOutlet: 0,
            TakingsByCurrency: [],
            PriorCalls: null, PriorCallsAgainstPlan: null, PriorPlannedCalls: null,
            PriorProductiveCalls: 0, PriorOutletsBought: 0,
            PriorTakingsByCurrency: []);

        var mirrored = RoundTrip<VanSalesScorecardRowResult, VanSalesScorecardRow>(row);

        Assert.Equal(expected, row.Band);

        // By name, not by value: the two enums are declared independently, and matching names is
        // what the wire actually needs — a reordered member would serialise to the wrong band.
        Assert.Equal(expected.ToString(), mirrored.Band.ToString());
    }

    /// <summary>
    /// The caveats are two hand-written copies of the same prose in two projects. A reader has no way
    /// to tell which copy is in front of them, so they have to be the same words.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0, 0, false)]
    [InlineData(3, 2, 1, 1, 4, 2)]
    [InlineData(5, 0, 3, 0, 0, false)]
    [InlineData(1, 1, 0, 7, 0, true)]
    public void The_caveats_are_word_for_word_identical_on_both_sides(
        int rowCount,
        int unratedRows,
        int rowsWithNoPriorPeriod,
        int rowsWithNoPlan,
        int salesWithoutTender,
        object priorPeriodEmpty)
    {
        var empty = priorPeriodEmpty is bool flag ? flag : Convert.ToInt32(priorPeriodEmpty) > 1;

        var quality = new VanSalesScorecardQualityResult(
            RowCount: rowCount,
            UnratedRows: unratedRows,
            RowsWithNoPriorPeriod: rowsWithNoPriorPeriod,
            RowsWithNoPlan: rowsWithNoPlan,
            SalesWithoutTender: salesWithoutTender,
            SalesWithoutOutlet: 0,
            PriorPeriodEmpty: empty);

        var mirrored = RoundTrip<VanSalesScorecardQualityResult, VanSalesScorecardQuality>(quality);

        Assert.Equal(quality.Caveats, mirrored.Caveats);
        Assert.Equal(quality.IsClean, mirrored.IsClean);
    }

    /// <summary>
    /// Both unconditional caveats fire in every period, so a clean scorecard still says what it will
    /// not tell you. A page that gated its caveat block on IsClean would hide them.
    /// </summary>
    [Fact]
    public void A_clean_period_still_carries_the_two_standing_caveats()
    {
        var quality = new VanSalesScorecardQualityResult(5, 0, 0, 0, 0, 0, PriorPeriodEmpty: false);
        var mirrored = RoundTrip<VanSalesScorecardQualityResult, VanSalesScorecardQuality>(quality);

        Assert.True(mirrored.IsClean);
        Assert.Equal(2, mirrored.Caveats.Count());
        Assert.Contains(mirrored.Caveats, caveat => caveat.Contains("never ranked"));
        Assert.Contains(mirrored.Caveats, caveat => caveat.Contains("the report is right and this is a bug"));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────

    private static VanSalesScorecardReportResult Populated() =>
        new(
            FromDate: new DateTime(2026, 8, 10),
            ToDate: new DateTime(2026, 8, 16),
            PriorFromDate: new DateTime(2026, 8, 3),
            PriorToDate: new DateTime(2026, 8, 9),
            Grouping: ApiGrouping.Route,
            CallComplianceTarget: 0.95,
            StrikeRateTarget: 0.75,
            Summary: new VanSalesScorecardSummaryResult(
                RowCount: 3, GreenCount: 1, AmberCount: 1, RedCount: 0, UnratedCount: 1,
                TradingDays: 6,
                Calls: 120, CallsAgainstPlan: 100, PlannedCalls: 140,
                ProductiveCalls: 90, OutletsBought: 74, NewOutlets: 6, Kilometres: 1180,
                PriorCalls: 110, PriorCallsAgainstPlan: 96, PriorPlannedCalls: 140,
                PriorProductiveCalls: 77, PriorOutletsBought: 68,
                TakingsByCurrency:
                [
                    new VanSalesMoneyResult("USD", DocumentCount: 90, DropCount: 74, Gross: 4120.75m),
                    new VanSalesMoneyResult("ZWG", DocumentCount: 12, DropCount: 11, Gross: 39_400m)
                ]),
            Rows:
            [
                new VanSalesScorecardRowResult(
                    Key: "GURUVE",
                    Label: "Guruve",
                    SubLabel: "Mash Central",
                    UserId: RepId,
                    CallComplianceTarget: 0.95,
                    StrikeRateTarget: 0.75,
                    TradingDays: 5,
                    Calls: 40, CallsAgainstPlan: 36, PlannedCalls: 40,
                    ProductiveCalls: 30, OutletsBought: 28, NewOutlets: 3, Kilometres: 410,
                    SalesWithoutTender: 2, SalesWithoutOutlet: 1,
                    TakingsByCurrency:
                    [
                        new VanSalesMoneyResult("USD", DocumentCount: 30, DropCount: 28, Gross: 1234.50m)
                    ],
                    PriorCalls: 38, PriorCallsAgainstPlan: 35, PriorPlannedCalls: 40,
                    PriorProductiveCalls: 26, PriorOutletsBought: 25,
                    PriorTakingsByCurrency:
                    [
                        new VanSalesMoneyResult("USD", DocumentCount: 26, DropCount: 25, Gross: 1100m)
                    ]),
                new VanSalesScorecardRowResult(
                    Key: "MUTOKO",
                    Label: "Mutoko",
                    SubLabel: "Mash East",
                    UserId: null,
                    CallComplianceTarget: 0.95,
                    StrikeRateTarget: 0.75,
                    TradingDays: 4,
                    Calls: 30, CallsAgainstPlan: 24, PlannedCalls: 40,
                    ProductiveCalls: 20, OutletsBought: 19, NewOutlets: 2, Kilometres: 380,
                    SalesWithoutTender: 0, SalesWithoutOutlet: 0,
                    TakingsByCurrency:
                    [
                        new VanSalesMoneyResult("ZWG", DocumentCount: 12, DropCount: 11, Gross: 39_400m)
                    ],
                    PriorCalls: 28, PriorCallsAgainstPlan: 24, PriorPlannedCalls: 40,
                    PriorProductiveCalls: 21, PriorOutletsBought: 20,
                    PriorTakingsByCurrency: []),
                new VanSalesScorecardRowResult(
                    Key: "«no departure record»",
                    Label: "No departure record",
                    SubLabel: "Nothing on these sales says which route they were made on",
                    UserId: null,
                    CallComplianceTarget: 0.95,
                    StrikeRateTarget: 0.75,
                    TradingDays: 2,
                    Calls: null, CallsAgainstPlan: null, PlannedCalls: null,
                    ProductiveCalls: 4, OutletsBought: 4, NewOutlets: 0, Kilometres: null,
                    SalesWithoutTender: 1, SalesWithoutOutlet: 0,
                    TakingsByCurrency:
                    [
                        new VanSalesMoneyResult("USD", DocumentCount: 4, DropCount: 4, Gross: 210.25m)
                    ],
                    PriorCalls: null, PriorCallsAgainstPlan: null, PriorPlannedCalls: null,
                    PriorProductiveCalls: 0, PriorOutletsBought: 0,
                    PriorTakingsByCurrency: [])
            ],
            TakingsMovement:
            [
                new VanSalesScorecardMovementResult("USD", Gross: 4120.75m, PriorGross: 3800m),
                new VanSalesScorecardMovementResult("ZWG", Gross: 39_400m, PriorGross: null)
            ],
            Quality: new VanSalesScorecardQualityResult(
                RowCount: 3, UnratedRows: 1, RowsWithNoPriorPeriod: 1, RowsWithNoPlan: 1,
                SalesWithoutTender: 3, SalesWithoutOutlet: 1, PriorPeriodEmpty: false));
}
