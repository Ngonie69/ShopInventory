using System.Text.Json;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Serialises the API's ADR report and reads it back as the portal's hand-mirrored DTO, as the wire
/// does. See <see cref="VanSalesPerformanceContractTests"/> for why: a nullability mismatch does not
/// fail loudly, it makes the page say "no data".
/// </summary>
public class AdrPerformanceContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly Guid Adr = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static AdrPerformanceReportResponse RoundTrip(AdrPerformanceReportResult result)
    {
        var json = JsonSerializer.Serialize(result, Wire);
        var mirrored = JsonSerializer.Deserialize<AdrPerformanceReportResponse>(json, Wire);

        Assert.NotNull(mirrored);
        return mirrored;
    }

    [Fact]
    public void A_populated_report_crosses_the_wire_intact()
    {
        var mirrored = RoundTrip(Populated());

        Assert.Equal(20, mirrored.Overall.Calls);
        Assert.Equal(0.5, mirrored.Overall.StrikeRate);
        Assert.Equal(0.4, Assert.Single(mirrored.Overall.Orders).Share!.Value, 3);

        var row = Assert.Single(mirrored.Adrs);
        Assert.Equal(10, row.ProductiveCalls);
        Assert.Equal(0.5, row.StrikeRate);

        var adrs = Assert.Single(mirrored.OrdersByChannel, channel => channel.IsAdr);
        Assert.Equal(0.3, Assert.Single(adrs.Shares).Share!.Value, 3);

        var detail = Assert.IsType<AdrPerformanceDetail>(mirrored.Detail);
        Assert.Equal(Adr, detail.UserId);
        var shop = Assert.Single(detail.Shops);
        Assert.Equal("Shop one", shop.DisplayName);
        Assert.Equal(new DateTime(2026, 9, 30), shop.LastActiveOn);
        Assert.Equal(24m, Assert.Single(Assert.Single(detail.Items).QuantitiesByUoM).Quantity);
        var order = Assert.Single(detail.Orders);
        Assert.Equal(5001, order.SapDocNum);
        Assert.Equal("In SAP", order.Stage);
        Assert.Equal(7, detail.OrdersNotListed);

        Assert.Equal("Caveat", Assert.Single(mirrored.Caveats));
    }

    /// <summary>Every nullable the API can send as null, sent as null at once.</summary>
    [Fact]
    public void Every_null_the_API_can_send_is_read_as_null_rather_than_breaking_the_page()
    {
        var mirrored = RoundTrip(new AdrPerformanceReportResult(
            new DateTime(2026, 9, 30),
            new DateTime(2026, 9, 30),
            new AdrPerformanceOverallResult(1, 0, new AdrOrderCountsResult(0, 0, 0, 0, 0), [], [], null, 0),
            [
                new AdrPerformanceRepResult(Adr, "adr01", null, null, true, 0,
                    new AdrOrderCountsResult(0, 0, 0, 0, 0), 0, [], 0, [], [new AdrShareResult("USD", null, null)], null, 0)
            ],
            [new AdrChannelResult("ADRs", true, 0, 0, [new AdrChannelShareResult("USD", 0m, null)])],
            new AdrPerformanceDetailResult(Adr, [],
                [new AdrItemResult(1, "CHE011", null, 1, 1, [new VanSalesQuantityResult(null, 1m, 1)], [])],
                [new AdrOrderResult(1, "SO-1", null, new DateTime(2026, 9, 30), null, null, "Pending", "Not yet in SAP", "USD", 0m, 0)],
                0),
            []));

        Assert.Null(mirrored.Overall.Calls);
        Assert.Null(mirrored.Overall.StrikeRate);
        Assert.Null(Assert.Single(mirrored.Adrs).StrikeRate);
        Assert.Null(Assert.Single(mirrored.OrdersByChannel).Shares.Single().Share);

        var detail = Assert.IsType<AdrPerformanceDetail>(mirrored.Detail);
        Assert.Null(Assert.Single(detail.Items).ItemDescription);
        var order = Assert.Single(detail.Orders);
        Assert.Null(order.SapDocNum);
        Assert.Equal("No shop recorded", order.DisplayCustomer);
    }

    [Fact]
    public void A_report_for_every_ADR_carries_no_detail()
    {
        var populated = Populated();

        Assert.Null(RoundTrip(populated with { Detail = null }).Detail);
    }

    private static AdrPerformanceReportResult Populated()
    {
        var usd = new VanSalesMoneyResult("USD", 12, 10, 1200m);

        return new AdrPerformanceReportResult(
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 30),
            new AdrPerformanceOverallResult(
                1, 1, new AdrOrderCountsResult(12, 4, 3, 7, 1),
                [new AdrContributionResult("USD", 12, 1200m, 30, 3000m)],
                [],
                20,
                10),
            [
                new AdrPerformanceRepResult(Adr, "adr01", "Tendai Ncube", "VAN015", true, 6,
                    new AdrOrderCountsResult(12, 4, 3, 7, 1), 6, [usd], 0, [],
                    [new AdrShareResult("USD", 0.4, null)], 20, 10)
            ],
            [
                new AdrChannelResult("Van sales reps", false, 30, 4, [new AdrChannelShareResult("USD", 2800m, 0.7)]),
                new AdrChannelResult("ADRs", true, 12, 1, [new AdrChannelShareResult("USD", 1200m, 0.3)])
            ],
            new AdrPerformanceDetailResult(
                Adr,
                [new AdrShopResult("SHOP1", "Shop one", 3, [usd], 0, [], new DateTime(2026, 9, 30))],
                [
                    new AdrItemResult(1, "CHE011", "Cheddar 1kg", 5, 3,
                        [new VanSalesQuantityResult(null, 24m, 5)],
                        [new VanSalesLineMoneyResult("USD", 5, 240m)])
                ],
                [new AdrOrderResult(41, "SO-0041", 5001, new DateTime(2026, 9, 30), "SHOP1", "Shop one", "Approved", "In SAP", "USD", 120m, 3)],
                7),
            ["Caveat"]);
    }
}
