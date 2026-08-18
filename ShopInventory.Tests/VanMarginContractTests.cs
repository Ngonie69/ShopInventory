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
/// This report's nulls are unusual in that three of them are meant to be permanent.
/// <c>UnitCost</c>, <c>CostByCurrency</c> and <c>MarginByCurrency</c> are null on every row and stay
/// null until a cost source is connected — and a mirror that turned any of them into a zero would
/// report the vans selling at exactly cost, which is a finding rather than an absence and would be
/// indistinguishable from the real thing once the cost lands. Those are pinned harder than anything
/// else here.
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
    /// The three cost fields are null and have to arrive null. A zero in any of them says the vans
    /// sell at cost.
    /// </summary>
    [Fact]
    public void The_cost_and_margin_fields_arrive_null_and_never_zero()
    {
        var mirrored = RoundTrip<VanMarginReportResult, VanMarginReportResponse>(Populated());

        Assert.False(mirrored.Summary.MarginAvailable);

        Assert.All(mirrored.Items, item =>
        {
            Assert.Null(item.UnitCost);
            Assert.Null(item.CostByCurrency);
            Assert.Null(item.MarginByCurrency);
        });
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
            Summary: new VanMarginSummaryResult(0, 0, 0, 0, [], [], []),
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
                    CostByCurrency: null)
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
            Quality: new VanMarginQualityResult(0, 0, 1, PostingJobEnabled: false));

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
            Summary: new VanMarginSummaryResult(0, 0, 0, 0, [], [], []),
            Items: [],
            Vans: [],
            Quality: new VanMarginQualityResult(0, 0, 0, PostingJobEnabled: false));

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
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 0, 0, true)]
    [InlineData(100, 40, 3, false)]
    [InlineData(100, 100, 0, true)]
    [InlineData(7, 0, 1, false)]
    public void The_caveats_are_word_for_word_identical_on_both_sides(
        int lineCount,
        int postedLineCount,
        int itemsWithNoDescription,
        bool postingJobEnabled)
    {
        var quality = new VanMarginQualityResult(
            lineCount, postedLineCount, itemsWithNoDescription, postingJobEnabled);

        var mirrored = RoundTrip<VanMarginQualityResult, VanMarginQuality>(quality);

        Assert.Equal(quality.Caveats, mirrored.Caveats);
        Assert.Equal(quality.UnpostedLineCount, mirrored.UnpostedLineCount);
        Assert.Equal(quality.IsClean, mirrored.IsClean);
    }

    /// <summary>
    /// The report can never call itself clean, on either side. It is named for a figure it does not
    /// carry, and the first caveat always says so.
    /// </summary>
    [Fact]
    public void The_report_is_never_clean_and_always_says_margin_is_missing()
    {
        var quality = new VanMarginQualityResult(100, 100, 0, PostingJobEnabled: true);
        var mirrored = RoundTrip<VanMarginQualityResult, VanMarginQuality>(quality);

        Assert.False(mirrored.IsClean);
        Assert.StartsWith("Margin is not computed.", mirrored.Caveats.First());
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
                QuantitiesByUoM: [new VanSalesQuantityResult(null, Quantity: 120m, LineCount: 10)]),
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
                    UnitCost: null,
                    CostByCurrency: null),
                new VanMarginItemResult(
                    ItemCode: "NRI049",
                    ItemDescription: null,
                    LineCount: 4,
                    PostedLineCount: 1,
                    VanCount: 1,
                    RevenueByCurrency: [new VanSalesLineMoneyResult("USD", 4, 300m)],
                    CostableRevenueByCurrency: [new VanSalesLineMoneyResult("USD", 1, 50m)],
                    QuantitiesByUoM: [new VanSalesQuantityResult(null, 50m, 4)],
                    UnitCost: null,
                    CostByCurrency: null)
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
            Quality: new VanMarginQualityResult(
                LineCount: 10,
                PostedLineCount: 4,
                ItemsWithNoDescription: 1,
                PostingJobEnabled: false));
}
