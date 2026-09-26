using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CountVariance;
using ShopInventory.Features.CountVariance.Queries.GetCountVariance;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Count variance valued at selling price: the variance is SAP's, the price is the van sales list's,
/// and neither an uncounted line nor an unpriced one may pass for something it is not.
/// </summary>
public class CountVarianceTests
{
    private static InventoryCountingLine Line(
        int lineNumber, string item, decimal inWarehouse, decimal? counted, string warehouse = "VAN006")
        => new()
        {
            LineNumber = lineNumber,
            VisualOrder = lineNumber - 1,
            ItemCode = item,
            ItemDescription = item + " description",
            WarehouseCode = warehouse,
            InWarehouseQuantity = inWarehouse,
            Counted = counted is null ? SapYesNo.No : SapYesNo.Yes,
            CountedQuantity = counted ?? 0m,
            // What SAP itself reports: zero on a line nobody has counted.
            Variance = counted is null ? 0m : counted.Value - inWarehouse
        };

    private static readonly Dictionary<string, decimal> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ICC025"] = 1.10m,
        ["MUE002"] = 3.60m,
        ["VHU002"] = 0.30m,
        ["YOG063"] = 1.75m,
        ["CRA002"] = 0m, // a zero row: the list does not sell crates
    };

    [Fact]
    public void Values_short_and_over_lines_at_selling_price()
    {
        var (lines, totals) = CountVarianceValuation.Value(
        [
            Line(1, "ICC025", 30, 28),
            Line(2, "MUE002", 30, 31),
            Line(3, "VHU002", 658, 640),
        ], Prices);

        Assert.Equal(-2.20m, lines[0].VarianceValue);
        Assert.Equal(CountVarianceLineStatus.Short, lines[0].Status);
        Assert.Equal(3.60m, lines[1].VarianceValue);
        Assert.Equal(CountVarianceLineStatus.Over, lines[1].Status);
        Assert.Equal(-5.40m, lines[2].VarianceValue);

        Assert.Equal(-7.60m, totals.ShortValue);
        Assert.Equal(3.60m, totals.OverValue);
        Assert.Equal(-4.00m, totals.NetValue);
        Assert.Equal(20m, totals.ShortQuantity);
        Assert.Equal(1m, totals.OverQuantity);
        Assert.Equal(2, totals.ShortLines);
        Assert.Equal(1, totals.OverLines);
        // 30 × 1.10 + 30 × 3.60 + 658 × 0.30
        Assert.Equal(338.40m, totals.StockValue);
    }

    [Fact]
    public void An_uncounted_line_is_not_a_match()
    {
        var (lines, totals) = CountVarianceValuation.Value([Line(1, "YOG063", 45, null)], Prices);

        var line = Assert.Single(lines);
        Assert.Equal(CountVarianceLineStatus.NotCounted, line.Status);
        Assert.Null(line.CountedQuantity);
        Assert.Equal(0m, line.Variance);
        Assert.Equal(1, totals.NotCountedLines);
        Assert.Equal(0, totals.MatchedLines);
        Assert.Equal(0m, totals.NetValue);
    }

    [Fact]
    public void A_counted_line_with_no_variance_is_a_match()
    {
        var (lines, totals) = CountVarianceValuation.Value([Line(1, "YOG063", 45, 45)], Prices);

        Assert.Equal(CountVarianceLineStatus.Matched, Assert.Single(lines).Status);
        Assert.Equal(1, totals.MatchedLines);
    }

    [Fact]
    public void A_zero_or_missing_price_leaves_the_line_unvalued_rather_than_free()
    {
        var (lines, totals) = CountVarianceValuation.Value(
        [
            Line(1, "CRA002", 7, 6),   // zero on the list
            Line(2, "NOPE01", 4, 1),   // absent from the list
            Line(3, "ICC025", 30, 28),
        ], Prices);

        Assert.All(lines.Take(2), line =>
        {
            Assert.Null(line.SellingPrice);
            Assert.Null(line.VarianceValue);
            Assert.Equal(CountVarianceLineStatus.Short, line.Status);
        });

        Assert.Equal(2, totals.UnpricedLines);
        Assert.Equal(2, totals.UnvaluedVarianceLines);
        Assert.Equal(3, totals.ShortLines);
        // Only the priced line reaches the money.
        Assert.Equal(-2.20m, totals.ShortValue);
        Assert.Equal(33.00m, totals.StockValue);
    }

    [Fact]
    public void Lines_follow_the_order_b1_shows_them_in()
    {
        var first = Line(7, "ICC025", 1, 1);
        first.VisualOrder = 0;
        var second = Line(2, "MUE002", 1, 1);
        second.VisualOrder = 1;

        var (lines, _) = CountVarianceValuation.Value([second, first], Prices);

        Assert.Equal(["ICC025", "MUE002"], lines.Select(line => line.ItemCode));
        Assert.Equal([1, 2], lines.Select(line => line.RowNumber));
    }

    [Theory]
    [InlineData("2026-09-26T00:00:00Z", 2026, 9, 26)]
    [InlineData("2023-12-31T00:00:00", 2023, 12, 31)]
    public void The_count_date_is_a_calendar_date_no_time_zone_can_move(string sap, int year, int month, int day)
        => Assert.Equal(new DateTime(year, month, day), CountVarianceValuation.ParseCountDate(sap));

    [Fact]
    public async Task The_report_values_the_count_at_the_van_sales_price_list()
    {
        const int vanList = 11;
        int? listAskedFor = null;

        var count = new InventoryCounting
        {
            DocumentEntry = 6120,
            DocumentNumber = 2679,
            CountDate = "2026-09-26T00:00:00Z",
            CountTime = "15:04:00",
            DocumentStatus = "cdsOpen",
            SingleCounterType = "ctUser",
            SingleCounterID = 63,
            InventoryCountingLines = [Line(1, "ICC025", 30, 28), Line(2, "YOG063", 45, null)]
        };

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInventoryCountingAsync) => Task.FromResult<InventoryCounting?>(count),
            nameof(ISAPServiceLayerClient.GetSapUsersAsync) => Task.FromResult(new List<SAPUser>
            {
                new() { InternalKey = 63, UserCode = "crispen", UserName = "Crispen" }
            }),
            _ => null
        });

        var catalogue = StubProxy.For<ILocalPriceCatalogService>((method, args) =>
        {
            if (method.Name != nameof(ILocalPriceCatalogService.GetPricesByPriceListAsync))
                return null;

            listAskedFor = (int)args![0]!;
            return Task.FromResult(new ItemPricesByListResponseDto
            {
                PriceListNum = vanList,
                PriceListName = "Van sales",
                Currency = "USD",
                Prices = [new ItemPriceByListDto { ItemCode = "ICC025", Price = 1.10m }]
            });
        });

        var policy = StubProxy.For<IVanSalesOrderingPolicy>((method, _) =>
            method.Name == nameof(IVanSalesOrderingPolicy.GetRulesAsync)
                ? Task.FromResult(new VanSalesOrderingRules(8, vanList, 10m))
                : null);

        var handler = new GetCountVarianceHandler(
            sap, catalogue, policy,
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetCountVarianceHandler>.Instance);

        var result = await handler.Handle(new GetCountVarianceQuery(6120), CancellationToken.None);

        Assert.False(result.IsError);
        var report = result.Value;
        Assert.Equal(vanList, listAskedFor);
        Assert.Equal(vanList, report.PriceListNum);
        Assert.Equal("USD", report.Currency);
        Assert.Equal(["VAN006"], report.Warehouses);
        Assert.Equal("Crispen", report.Document.CounterName);
        Assert.Equal("Open", report.Document.Status);
        Assert.Equal("15:04", report.Document.CountTime);
        Assert.Equal(-2.20m, report.Totals.NetValue);
        Assert.Equal(1, report.Totals.NotCountedLines);
    }

    [Fact]
    public async Task A_count_sap_does_not_have_is_not_found()
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.GetInventoryCountingAsync)
                ? Task.FromResult<InventoryCounting?>(null)
                : null);

        var handler = new GetCountVarianceHandler(
            sap,
            StubProxy.Unused<ILocalPriceCatalogService>(),
            StubProxy.Unused<IVanSalesOrderingPolicy>(),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetCountVarianceHandler>.Instance);

        var result = await handler.Handle(new GetCountVarianceQuery(1), CancellationToken.None);

        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
    }
}
