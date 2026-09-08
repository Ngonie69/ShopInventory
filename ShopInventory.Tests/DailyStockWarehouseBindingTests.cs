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
/// The consequence is that <c>FetchDailyStockHandler</c> walks every monitored warehouse twice per
/// run, so the 07:00 snapshot reads SAP twice for each one against a six-slot pool. This test pins
/// the binder's behaviour rather than that consequence, so a framework change that fixes it is
/// noticed here instead of quietly halving the snapshot's workload.
/// </remarks>
public sealed class DailyStockWarehouseBindingTests
{
    [Fact]
    public void Binding_over_a_defaulted_list_appends_rather_than_replaces()
    {
        var settings = new DailyStockSettings();
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
}
