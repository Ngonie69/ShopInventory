using System.Text.Json;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetVanMarginReport;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Sends the margin report across the wire and reads it back as the portal's hand-mirrored DTOs.
/// </summary>
/// <remarks>
/// Same guard as the other van reports: a property declared non-nullable on the portal side against
/// a value the API can send as null makes System.Text.Json throw inside <c>GetFromJsonAsync</c>, the
/// service's catch turns that into null, and the page reports "no data".
///
/// The null that matters most here is <c>UnitCost</c>. It is null when no cost was found and must
/// never arrive as zero: a zero unit cost reports an item that costs nothing to sell, and B1 does
/// leave the column at zero on a line whose item has no valuation — so the difference between "we do
/// not know" and "it is free" is the difference between an honest gap and a fabricated profit.
///
/// The margin list is the other. A currency with no matching cost gets no row at all, so an empty
/// list means "not established" and never "broke even"; a mirror that defaulted it to a zero-valued
/// row would turn every uncosted currency into a break-even claim.
///
/// The caveats are two hand-written copies of the same prose in two projects. Nothing makes them
/// agree, and a reader cannot tell which copy is on the screen in front of them.
/// </remarks>
public class VanMarginContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static T RoundTrip<TSource, T>(TSource result)
    {
        var mirrored = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result, Wire), Wire);
        Assert.NotNull(mirrored);
        return mirrored;
    }

    [Fact]
    public void A_populated_report_crosses_the_wire_intact()
    {
        var mirrored = RoundTrip<VanMarginReportResult, VanMarginReportResponse>(Populated());

        Assert.Equal(new DateTime(2026, 8, 1), mirrored.FromDate);
        Assert.Equal(new DateTime(2026, 8, 31), mirrored.ToDate);

        Assert.Equal(2, mirrored.Summary.ItemCount);
        Assert.Equal(2, mirrored.Summary.VanCount);
        Assert.Equal(10, mirrored.Summary.LineCount);
        Assert.Equal(4, mirrored.Summary.PostedLineCount);
        Assert.Equal(0.4, mirrored.Summary.CostableLineShare);

        var revenue = Assert.Single(mirrored.Summary.RevenueByCurrency);
        Assert.Equal("USD", revenue.Currency);
        Assert.Equal(1000m, revenue.Gross);

        var item = mirrored.Items.Single(row => row.ItemCode == "CHE011");
        Assert.Equal("Gouda 1kg", item.ItemDescription);
        Assert.Equal("Gouda 1kg", item.DisplayName);
        Assert.Equal(2, item.VanCount);

        var van = mirrored.Vans.Single(row => row.WarehouseCode == "VAN010");
        Assert.Equal("Tendai Moyo", van.DisplayName);
        Assert.Equal(6, van.LineCount);
    }

    /// <summary>
    /// An item with no cost arrives with a null unit cost and an empty margin list — never a zero
    /// cost and never a zero-valued margin row. The first would report an item that costs nothing to
    /// sell; the second would report it breaking even.
    /// </summary>
    [Fact]
    public void An_uncosted_item_arrives_as_an_absence_and_not_as_a_zero()
    {
        var mirrored = RoundTrip<VanMarginReportResult, VanMarginReportResponse>(Populated());

        var costed = mirrored.Items.Single(item => item.ItemCode == "CHE011");
        Assert.Equal(8.25m, costed.UnitCost);
        Assert.True(costed.HasCost);
        Assert.Equal(140m, Assert.Single(costed.MarginByCurrency).Margin);

        var uncosted = mirrored.Items.Single(item => item.ItemCode == "NRI049");
        Assert.Null(uncosted.UnitCost);
        Assert.False(uncosted.HasCost);
        Assert.Empty(uncosted.MarginByCurrency);
    }

    /// <summary>
    /// The margin arithmetic is re-derived on the portal side, so both copies have to agree — and
    /// the rate has to survive as a rate rather than being recomputed from rounded money.
    /// </summary>
    [Fact]
    public void The_margin_arithmetic_agrees_on_both_sides()
    {
        var source = Populated();
        var mirrored = RoundTrip<VanMarginReportResult, VanMarginReportResponse>(source);

        var api = Assert.Single(source.Summary.MarginByCurrency);
        var web = Assert.Single(mirrored.Summary.MarginByCurrency);

        Assert.Equal(api.Margin, web.Margin);
        Assert.Equal(api.MarginRate, web.MarginRate);
        Assert.Equal(160m, web.Margin);
        Assert.Equal(0.4, web.MarginRate);
        Assert.True(mirrored.Summary.MarginAvailable);
    }

    /// <summary>
    /// Margin per kilometre is derived on both sides from the same two figures, and a route whose
    /// odometer was never read has none — not a rate of zero, which would report a route that drove
    /// nowhere and earned anyway.
    /// </summary>
    [Fact]
    public void Margin_per_kilometre_survives_and_is_absent_where_the_distance_is()
    {
        var mirrored = RoundTrip<VanMarginReportResult, VanMarginReportResponse>(Populated());

        var route = mirrored.Routes.Single(row => row.RouteCode == "GURUVE");
        Assert.Equal(400, route.Kilometres);

        var rate = Assert.Single(route.MarginPerKilometre);
        Assert.Equal("USD", rate.Currency);
        // 160 margin over 400 km.
        Assert.Equal(0.4m, rate.MarginPerKilometre);

        var unattributed = mirrored.Routes.Single(row => row.RouteName == "No departure record");
        Assert.Null(unattributed.Kilometres);
        Assert.Empty(unattributed.MarginPerKilometre);
        Assert.Empty(unattributed.MarginByCurrency);

        // And its revenue is still counted, so the route rows account for the period.
        Assert.Equal(200m, Assert.Single(unattributed.RevenueByCurrency).Gross);
    }

    /// <summary>
    /// The page must never call a route figure a profit. Both copies of the caveat say contribution.
    /// </summary>
    [Fact]
    public void A_report_with_routes_says_the_figure_is_contribution_and_not_profit()
    {
        var mirrored = RoundTrip<VanMarginReportResult, VanMarginReportResponse>(Populated());

        Assert.Contains(
            mirrored.Quality.Caveats,
            caveat => caveat.Contains("contribution, not profitability"));
        Assert.Contains(
            mirrored.Quality.Caveats,
            caveat => caveat.Contains("no departure record"));
    }

    /// <summary>
    /// The nullables that are absences rather than zeroes: a description nobody recorded, a rep
    /// nobody resolved, and a costable share for a period that sold nothing.
    /// </summary>
    [Fact]
    public void Every_null_the_api_can_send_survives_the_mirror()
    {
        var result = new VanMarginReportResult(
            FromDate: new DateTime(2026, 8, 1),
            ToDate: new DateTime(2026, 8, 31),
            Summary: new VanMarginSummaryResult(0, 0, 0, 0, [], [], [], null, []),
            Items:
            [
                new VanMarginItemResult(
                    ItemCode: "UNKNOWN",
                    ItemDescription: null,
                    LineCount: 0,
                    PostedLineCount: 0,
                    VanCount: 0,
                    RevenueByCurrency: [],
                    CostableRevenueByCurrency: [],
                    QuantitiesByUoM: [],
                    UnitCost: null,
                    CostCurrency: null,
                    MarginByCurrency: [])
            ],
            Vans:
            [
                new VanMarginVanResult(
                    WarehouseCode: "VAN099",
                    Username: null,
                    FullName: null,
                    ItemCount: 0,
                    LineCount: 0,
                    PostedLineCount: 0,
                    RevenueByCurrency: [],
                    CostableRevenueByCurrency: [])
            ],
            Routes: [],
            Quality: new VanMarginQualityResult(0, 0, 1, 1, 1, 0, 0, null, true, [], false));

        var mirrored = RoundTrip<VanMarginReportResult, VanMarginReportResponse>(result);

        Assert.Null(mirrored.Summary.CostableLineShare);

        var item = Assert.Single(mirrored.Items);
        Assert.Null(item.ItemDescription);
        Assert.Null(item.CostableLineShare);
        // Falls back to the code rather than to an empty cell.
        Assert.Equal("UNKNOWN", item.DisplayName);

        var van = Assert.Single(mirrored.Vans);
        Assert.Null(van.Username);
        Assert.Null(van.FullName);
        Assert.Null(van.CostableLineShare);
        Assert.Equal("VAN099", van.DisplayName);
    }

    [Fact]
    public void An_empty_report_arrives_with_empty_lists_and_a_null_share()
    {
        var result = new VanMarginReportResult(
            FromDate: new DateTime(2026, 8, 1),
            ToDate: new DateTime(2026, 8, 31),
            Summary: new VanMarginSummaryResult(0, 0, 0, 0, [], [], [], null, []),
            Items: [],
            Vans: [],
            Routes: [],
            Quality: new VanMarginQualityResult(0, 0, 0, 0, 0, 0, 0, null, true, [], false));

        var mirrored = RoundTrip<VanMarginReportResult, VanMarginReportResponse>(result);

        Assert.Empty(mirrored.Items);
        Assert.Empty(mirrored.Vans);
        Assert.Null(mirrored.Summary.CostableLineShare);
        Assert.False(mirrored.Quality.IsClean);
        Assert.NotEmpty(mirrored.Quality.Caveats);
    }

    /// <summary>
    /// The caveats are two hand-written copies of the same prose. A reader has no way to tell which
    /// copy is in front of them, so they have to be the same words.
    /// </summary>
    [Theory]
    // Nothing established at all.
    [InlineData(0, 0, 0, 0, 0, null, false, false)]
    // Fully costed and complete: the only shape that is clean.
    [InlineData(100, 100, 0, 0, 5, "USD", true, true)]
    // Costed, but over part of the period only.
    [InlineData(100, 40, 3, 2, 5, "USD", true, false)]
    // Cost fetched and refused.
    [InlineData(100, 100, 0, 5, 5, null, true, true)]
    // Cost not asked for.
    [InlineData(7, 0, 1, 1, 1, null, false, false)]
    public void The_caveats_are_word_for_word_identical_on_both_sides(
        int lineCount,
        int postedLineCount,
        int itemsWithNoDescription,
        int itemsWithoutCost,
        int itemCount,
        string? costCurrency,
        bool costAttempted,
        bool postingJobEnabled)
    {
        var quality = new VanMarginQualityResult(
            lineCount, postedLineCount, itemsWithNoDescription, itemsWithoutCost, itemCount,
            RouteCount: 2, LinesWithNoRoute: 1,
            costCurrency, costAttempted, costCurrency is null ? [] : ["ZWG"], postingJobEnabled);

        var mirrored = RoundTrip<VanMarginQualityResult, VanMarginQuality>(quality);

        Assert.Equal(quality.Caveats, mirrored.Caveats);
        Assert.Equal(quality.UnpostedLineCount, mirrored.UnpostedLineCount);
        Assert.Equal(quality.IsClean, mirrored.IsClean);
    }

    /// <summary>
    /// Clean means the margin describes the whole period. Both sides have to agree on that bar, or
    /// the page and the workbook disagree about whether a figure can be trusted whole.
    /// </summary>
    [Fact]
    public void A_fully_costed_period_is_clean_on_both_sides()
    {
        var quality = new VanMarginQualityResult(
            100, 100, 0, 0, 5, 0, 0, "USD", true, [], true);

        var mirrored = RoundTrip<VanMarginQualityResult, VanMarginQuality>(quality);

        Assert.True(quality.IsClean);
        Assert.True(mirrored.IsClean);
        Assert.Empty(mirrored.Caveats);
    }

    /// <summary>
    /// A period costed over half its lines is not clean, however good the margin looks. That is the
    /// distinction the whole report rests on.
    /// </summary>
    [Fact]
    public void A_partly_costed_period_is_not_clean()
    {
        var quality = new VanMarginQualityResult(
            100, 50, 0, 0, 5, 0, 0, "USD", true, [], true);

        var mirrored = RoundTrip<VanMarginQualityResult, VanMarginQuality>(quality);

        Assert.False(mirrored.IsClean);
        Assert.Contains(mirrored.Caveats, caveat => caveat.Contains("describes the remainder"));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────

    private static VanMarginReportResult Populated() =>
        new(
            FromDate: new DateTime(2026, 8, 1),
            ToDate: new DateTime(2026, 8, 31),
            Summary: new VanMarginSummaryResult(
                ItemCount: 2,
                VanCount: 2,
                LineCount: 10,
                PostedLineCount: 4,
                RevenueByCurrency: [new VanSalesLineMoneyResult("USD", LineCount: 10, Gross: 1000m)],
                CostableRevenueByCurrency:
                    [new VanSalesLineMoneyResult("USD", LineCount: 4, Gross: 400m)],
                QuantitiesByUoM: [new VanSalesQuantityResult(null, Quantity: 120m, LineCount: 10)],
                CostCurrency: "USD",
                MarginByCurrency:
                    [new VanMarginMoneyResult("USD", LineCount: 4, Revenue: 400m, Cost: 240m)]),
            Items:
            [
                new VanMarginItemResult(
                    ItemCode: "CHE011",
                    ItemDescription: "Gouda 1kg",
                    LineCount: 6,
                    PostedLineCount: 3,
                    VanCount: 2,
                    RevenueByCurrency: [new VanSalesLineMoneyResult("USD", 6, 700m)],
                    CostableRevenueByCurrency: [new VanSalesLineMoneyResult("USD", 3, 350m)],
                    QuantitiesByUoM: [new VanSalesQuantityResult(null, 70m, 6)],
                    UnitCost: 8.25m,
                    CostCurrency: "USD",
                    MarginByCurrency:
                        [new VanMarginMoneyResult("USD", LineCount: 3, Revenue: 350m, Cost: 210m)]),
                new VanMarginItemResult(
                    ItemCode: "NRI049",
                    ItemDescription: null,
                    LineCount: 4,
                    PostedLineCount: 1,
                    VanCount: 1,
                    RevenueByCurrency: [new VanSalesLineMoneyResult("USD", 4, 300m)],
                    CostableRevenueByCurrency: [new VanSalesLineMoneyResult("USD", 1, 50m)],
                    QuantitiesByUoM: [new VanSalesQuantityResult(null, 50m, 4)],
                    // No cost found for this one: it is the row that proves an absence survives.
                    UnitCost: null,
                    CostCurrency: "USD",
                    MarginByCurrency: [])
            ],
            Vans:
            [
                new VanMarginVanResult(
                    WarehouseCode: "VAN010",
                    Username: "van010",
                    FullName: "Tendai Moyo",
                    ItemCount: 2,
                    LineCount: 6,
                    PostedLineCount: 3,
                    RevenueByCurrency: [new VanSalesLineMoneyResult("USD", 6, 620m)],
                    CostableRevenueByCurrency: [new VanSalesLineMoneyResult("USD", 3, 310m)]),
                new VanMarginVanResult(
                    WarehouseCode: "VAN011",
                    Username: "van011",
                    FullName: null,
                    ItemCount: 1,
                    LineCount: 4,
                    PostedLineCount: 1,
                    RevenueByCurrency: [new VanSalesLineMoneyResult("USD", 4, 380m)],
                    CostableRevenueByCurrency: [new VanSalesLineMoneyResult("USD", 1, 90m)])
            ],
            Routes:
            [
                new VanMarginRouteResult(
                    RouteCode: "GURUVE",
                    RouteName: "Guruve",
                    Territory: "Mash Central",
                    VanCount: 1,
                    ItemCount: 2,
                    LineCount: 8,
                    PostedLineCount: 4,
                    Kilometres: 400,
                    RevenueByCurrency: [new VanSalesLineMoneyResult("USD", 8, 800m)],
                    CostableRevenueByCurrency: [new VanSalesLineMoneyResult("USD", 4, 400m)],
                    MarginByCurrency:
                        [new VanMarginMoneyResult("USD", LineCount: 4, Revenue: 400m, Cost: 240m)]),
                new VanMarginRouteResult(
                    RouteCode: "«no departure record»",
                    RouteName: "No departure record",
                    Territory: null,
                    VanCount: 1,
                    ItemCount: 1,
                    LineCount: 2,
                    PostedLineCount: 0,
                    // Nobody read an odometer for a day that was never opened.
                    Kilometres: null,
                    RevenueByCurrency: [new VanSalesLineMoneyResult("USD", 2, 200m)],
                    CostableRevenueByCurrency: [],
                    MarginByCurrency: [])
            ],
            Quality: new VanMarginQualityResult(
                LineCount: 10,
                PostedLineCount: 4,
                ItemsWithNoDescription: 1,
                ItemsWithoutCost: 1,
                ItemCount: 2,
                RouteCount: 1,
                LinesWithNoRoute: 2,
                CostCurrency: "USD",
                CostAttempted: true,
                CurrenciesWithoutMatchingCost: ["ZWG"],
                PostingJobEnabled: false));
}
