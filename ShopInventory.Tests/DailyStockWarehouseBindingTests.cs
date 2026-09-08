using Microsoft.Extensions.Configuration;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// What <c>DailyStock:MonitoredWarehouses</c> actually holds once configuration has bound over it.
/// </summary>
/// <remarks>
/// Found while building /transfer-listener, which reported KEFBYS twice against a configured list
/// that contains it once. The cause is the property initializer: the configuration binder appends to
/// a collection that already holds items rather than replacing it, so a list declared with defaults
/// in the settings class and also supplied in appsettings.json ends up holding both copies.
///
/// The consequence was that <c>FetchDailyStockHandler</c> walked every monitored warehouse twice per
/// run, reading SAP twice for each against a six-slot pool. Fixed by removing the initializers; the
/// tests here pin the binder's behaviour itself, which the fix depends on and does not change. If
/// either starts failing the framework has changed, and every `= []` declaration written for it
/// wants re-reading. <see cref="OptionsCollectionBindingTests"/> guards the declarations.
/// </remarks>
public sealed class DailyStockWarehouseBindingTests
{
    [Fact]
    public void Binding_over_a_defaulted_list_appends_rather_than_replaces()
    {
        // A local probe rather than DailyStockSettings, whose initializer is now deliberately empty:
        // this test is about the binder, and pointing it at the fixed class would assert nothing.
        var settings = new ListProbe();
        var defaultCount = settings.MonitoredWarehouses.Count;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DailyStock:MonitoredWarehouses:0"] = "KEFSHOP",
                ["DailyStock:MonitoredWarehouses:1"] = "KEFBYS"
            })
            .Build();

        configuration.GetSection(DailyStockSettings.SectionName).Bind(settings);

        // If this ever equals 2, the binder replaced the list and the duplication is gone. That is a
        // welcome change and a behavioural one: read the callers before deleting this test.
        Assert.Equal(defaultCount + 2, settings.MonitoredWarehouses.Count);
    }

    /// <summary>
    /// An array behaves the same way, which is not obvious: arrays and lists bind through different
    /// code paths, and it would be reasonable to assume a fixed-length target must be replaced.
    /// </summary>
    [Fact]
    public void An_array_property_with_an_initializer_appends_too()
    {
        var probe = new ArrayProbe();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Probe:Values:0"] = "x",
                ["Probe:Values:1"] = "y"
            })
            .Build();

        configuration.GetSection("Probe").Bind(probe);

        Assert.Equal(["a", "b", "x", "y"], probe.Values);
    }

    private sealed class ListProbe
    {
        public List<string> MonitoredWarehouses { get; set; } = ["KEFSHOP", "CORMACH", "KEFGRS"];
    }

    private sealed class ArrayProbe
    {
        public string[] Values { get; set; } = ["a", "b"];
    }
}
