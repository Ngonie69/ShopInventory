using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// SAP keeps an OITW row for every item in every warehouse, whether or not that warehouse has ever
/// handled the item. The low-stock report read all of them and asked one question — is on hand
/// below the threshold — so every item a van has never carried came back as critically out of
/// stock. On the morning this was written it counted 490,580 lines and called 490,100 of them
/// critical; 480 lines in the whole company had any stock on them at all. The alert raised from it
/// read "Low stock: 490580 item(s), 490100 critical. Lowest: wkc068 (0 in VAN011)".
///
/// A line now has to show the warehouse handles the item — something on hand, something committed,
/// or something on order — before its quantity is worth anything.
/// </summary>
public sealed class LowStockAlertScopingTests
{
    [Fact]
    public async Task AnItemTheWarehouseHasNeverHandledIsNotAShortage()
    {
        var report = await RunAsync(
            ("VAN011", [Stock("WKC068", onHand: 0, committed: 0, ordered: 0)]));

        Assert.Empty(report.Items);
        Assert.Equal(0, report.TotalAlerts);
        Assert.Equal(1, report.UnstockedLinesIgnored);
    }

    [Fact]
    public async Task StockRunningDownIsAShortage()
    {
        var report = await RunAsync(
            ("KEFGRC", [Stock("CHE011", onHand: 3, committed: 0, ordered: 0)]));

        var alert = Assert.Single(report.Items);
        Assert.Equal("CHE011", alert.ItemCode);
        Assert.Equal("KEFGRC", alert.WarehouseCode);
        Assert.Equal(3, alert.CurrentStock);
        Assert.Equal(0, report.UnstockedLinesIgnored);
    }

    /// <summary>
    /// Nothing on the shelf and customers waiting on it is the case the whole report exists for, so
    /// it has to survive a rule whose other job is to throw away lines that sit at zero.
    /// </summary>
    [Fact]
    public async Task NothingOnHandWithCustomersWaitingIsTheWorstCase()
    {
        var report = await RunAsync(
            ("KEFGRC", [Stock("CHE011", onHand: 0, committed: 40, ordered: 0)]));

        var alert = Assert.Single(report.Items);
        Assert.Equal(0, alert.CurrentStock);
        Assert.Equal("Critical", alert.AlertLevel);
    }

    [Fact]
    public async Task NothingOnHandWithReplenishmentComingIsStillReported()
    {
        var report = await RunAsync(
            ("KEFGRC", [Stock("CHE011", onHand: 0, committed: 0, ordered: 200)]));

        Assert.Single(report.Items);
        Assert.Equal(0, report.UnstockedLinesIgnored);
    }

    /// <summary>
    /// The shape of a real morning: a handful of genuine shortages against a warehouse-load of
    /// pairings that have never happened.
    /// </summary>
    [Fact]
    public async Task TheVanFullOfItemsItDoesNotCarryIsLeftOut()
    {
        var neverCarried = Enumerable
            .Range(0, 500)
            .Select(i => Stock($"SXF{i:000}", onHand: 0, committed: 0, ordered: 0))
            .ToList();

        var report = await RunAsync(
            ("VAN011", [.. neverCarried, Stock("SPY303", onHand: 2, committed: 0, ordered: 0)]),
            ("KEFGRC", [Stock("CHE011", onHand: 0, committed: 12, ordered: 0)]));

        Assert.Equal(2, report.TotalAlerts);
        Assert.Equal(500, report.UnstockedLinesIgnored);
        Assert.Equal(["CHE011", "SPY303"], report.Items.Select(i => i.ItemCode).Order().ToArray());
    }

    [Fact]
    public async Task StockAtOrAboveTheThresholdIsNeitherAlertedNorCountedAsUnstocked()
    {
        var report = await RunAsync(
            ("KEFGRC", [Stock("CHE011", onHand: 10, committed: 0, ordered: 0)]));

        Assert.Empty(report.Items);
        Assert.Equal(0, report.UnstockedLinesIgnored);
    }

    /// <summary>
    /// Critical is half the threshold, the same line
    /// <c>NotificationService.CreateLowStockAlertAsync</c> draws, rather than the 5 this used to
    /// hardcode — which tracked the default threshold of 10 by coincidence and turned every line
    /// critical the moment anyone lowered it.
    /// </summary>
    [Theory]
    [InlineData(4, "Critical")]
    [InlineData(5, "Warning")]
    [InlineData(9, "Warning")]
    public async Task TheCriticalLineFollowsTheThreshold(int onHand, string expectedAlertLevel)
    {
        var report = await RunAsync(
            ("KEFGRC", [Stock("CHE011", onHand, committed: 0, ordered: 0)]));

        Assert.Equal(expectedAlertLevel, Assert.Single(report.Items).AlertLevel);
    }

    [Fact]
    public async Task ALowerThresholdStillLeavesRoomForAWarning()
    {
        var report = await RunAsync(
            threshold: 4m,
            ("KEFGRC", [Stock("CHE011", onHand: 3, committed: 0, ordered: 0)]));

        Assert.Equal("Warning", Assert.Single(report.Items).AlertLevel);
    }

    [Fact]
    public async Task OneWarehouseCanBeAskedForOnItsOwn()
    {
        var report = await RunAsync(
            warehouseCode: "KEFGRC",
            threshold: null,
            ("VAN011", [Stock("SPY303", onHand: 2, committed: 0, ordered: 0)]),
            ("KEFGRC", [Stock("CHE011", onHand: 1, committed: 0, ordered: 0)]));

        Assert.Equal("CHE011", Assert.Single(report.Items).ItemCode);
    }

    private static StockQuantityDto Stock(string itemCode, decimal onHand, decimal committed, decimal ordered) =>
        new()
        {
            ItemCode = itemCode,
            ItemName = itemCode,
            InStock = onHand,
            Committed = committed,
            Ordered = ordered,
            Available = onHand - committed + ordered
        };

    private static Task<LowStockAlertReportDto> RunAsync(
        params (string WarehouseCode, List<StockQuantityDto> Stock)[] warehouses) =>
        RunAsync(warehouseCode: null, threshold: null, warehouses);

    private static Task<LowStockAlertReportDto> RunAsync(
        decimal? threshold,
        params (string WarehouseCode, List<StockQuantityDto> Stock)[] warehouses) =>
        RunAsync(warehouseCode: null, threshold, warehouses);

    private static Task<LowStockAlertReportDto> RunAsync(
        string? warehouseCode,
        decimal? threshold,
        params (string WarehouseCode, List<StockQuantityDto> Stock)[] warehouses)
    {
        var stockByWarehouse = warehouses.ToDictionary(
            w => w.WarehouseCode,
            w => w.Stock,
            StringComparer.OrdinalIgnoreCase);

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetWarehousesAsync) =>
                Task.FromResult(warehouses
                    .Select(w => new WarehouseDto { WarehouseCode = w.WarehouseCode, IsActive = true })
                    .ToList()),

            nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(stockByWarehouse[(string)args![0]!]),

            _ => throw new InvalidOperationException($"unexpected call {method.Name}")
        });

        var reports = new ReportService(
            sap,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ReportService>.Instance);

        return reports.GetLowStockAlertsAsync(warehouseCode, threshold);
    }
}
