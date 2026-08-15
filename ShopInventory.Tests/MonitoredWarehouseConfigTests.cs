using System.Text.Json;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the list of warehouses the daily snapshot covers.
/// </summary>
/// <remarks>
/// A desktop sale validates and deducts against today's snapshot, so a warehouse that is not
/// monitored has no snapshot, reads as zero stock, and refuses every sale made from it. Nothing
/// throws and nothing is logged as wrong — the first sign is a cashier who cannot sell.
///
/// The list exists twice: a default in <see cref="DailyStockSettings"/> and the deployed value in
/// appsettings.json. Configuration binding REPLACES a list rather than merging into it, so the
/// deployed file is the one that decides and the code default is only a fallback. Adding a
/// warehouse to one and not the other looks done and changes nothing — which is how KEFBYS, the
/// Bulawayo shop, came to be missing while KEFBYC, the depot one character away, was present.
/// </remarks>
public class MonitoredWarehouseConfigTests
{
    private static List<string> DeployedWarehouses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"appsettings.json should be in the test output at {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement
            .GetProperty("DailyStock")
            .GetProperty("MonitoredWarehouses")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToList();
    }

    [Fact]
    public void The_deployed_list_covers_everything_the_code_default_does()
    {
        // The check that would have caught KEFBYS. Because configuration replaces the list, a
        // warehouse present only in the code default is not monitored anywhere it matters.
        var deployed = DeployedWarehouses();
        var missing = new DailyStockSettings().MonitoredWarehouses
            .Where(warehouse => !deployed.Contains(warehouse))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "appsettings.json replaces the code default rather than merging with it, so these are "
            + $"unmonitored in every deployed environment: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("KEFBYS")]   // the Bulawayo SHOP
    [InlineData("KEFBYC")]   // the Bulawayo DEPOT the vans load from — a different warehouse
    [InlineData("KEFSHOP")]
    [InlineData("KEFGRS")]
    [InlineData("KEFGRC")]
    [InlineData("CORMACH2")]
    public void Every_shop_that_sells_is_monitored(string warehouse)
    {
        // Named one by one rather than counted, so adding a shop cannot quietly stand in for one
        // that was dropped.
        Assert.True(
            DeployedWarehouses().Contains(warehouse),
            $"sales from {warehouse} are refused at zero stock when it has no daily snapshot");
    }

    [Fact]
    public void The_two_Bulawayo_warehouses_are_both_present_and_are_not_the_same_entry()
    {
        // The failure mode this file exists for: two codes differing by one character, one of which
        // was assumed to cover the other.
        var deployed = DeployedWarehouses();

        Assert.Contains("KEFBYC", deployed);
        Assert.Contains("KEFBYS", deployed);
        Assert.Equal(2, deployed.Count(w => w is "KEFBYC" or "KEFBYS"));
    }

    [Fact]
    public void No_warehouse_is_listed_twice()
    {
        var deployed = DeployedWarehouses();

        Assert.Equal(deployed.Count, deployed.Distinct().Count());
    }
}
