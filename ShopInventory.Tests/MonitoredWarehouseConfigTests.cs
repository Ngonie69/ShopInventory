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
/// appsettings.json is the only source. <see cref="DailyStockSettings.MonitoredWarehouses"/> is
/// deliberately declared empty, because the configuration binder APPENDS to a collection that
/// already holds items rather than replacing it: while the class carried a 21-warehouse default and
/// appsettings.json listed the same 21, the bound list held 42 and the snapshot job read SAP twice
/// for every warehouse. <c>OptionsCollectionBindingTests</c> pins that.
///
/// So the expected list is written out below rather than compared against a code default. Both the
/// double-bind and the earlier miss of KEFBYS, the Bulawayo shop one character from KEFBYC, the
/// depot, came of the list living in two places at once.
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

    /// <summary>
    /// Every warehouse the daily snapshot is expected to cover. This is the list the class default
    /// used to hold; it lives here now, where dropping an entry fails a test instead of silently
    /// halving nothing and doubling the job.
    /// </summary>
    private static readonly string[] Expected =
    [
        "KEFSHOP", "CORMACH", "CORMACH2", "KEFGRS", "KEFGRC",
        "KEFBYC", "KEFBYS",
        "VAN001", "VAN004", "VAN005", "VAN006", "VAN008", "VAN009",
        "VAN010", "VAN011", "VAN012", "VAN013", "VAN014", "VAN015",
        "VAN016", "VAN018"
    ];

    [Fact]
    public void The_deployed_list_is_exactly_the_warehouses_the_snapshot_has_to_cover()
    {
        // Set comparison, not sequence: order carries no meaning, presence does. A warehouse
        // dropped from appsettings.json has no snapshot and refuses every sale made from it; one
        // added without being expected here is a change nobody wrote down.
        var deployed = DeployedWarehouses();

        var missing = Expected.Except(deployed).Order().ToList();
        var unexpected = deployed.Except(Expected).Order().ToList();

        Assert.True(
            missing.Count == 0,
            $"unmonitored in every deployed environment: {string.Join(", ", missing)}");
        Assert.True(
            unexpected.Count == 0,
            "monitored but not listed here — add it above if it is meant to be snapshotted: "
            + string.Join(", ", unexpected));
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
