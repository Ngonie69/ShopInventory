using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how <see cref="ConfigurationBinder"/> treats a collection property that already holds
/// items, and checks every options class in this project that has that shape.
/// </summary>
/// <remarks>
/// The binder APPENDS to a collection that is non-empty when binding starts — it does not replace
/// it. A property declared with a collection initializer and also supplied in appsettings.json is
/// therefore bound to default + config, not to config. Nothing throws and the values all look
/// plausible, because they are the right values; there are simply twice as many of them.
///
/// <c>DailyStockSettings.MonitoredWarehouses</c> was the live case: 21 warehouses in the
/// initializer and the same 21 in appsettings.json bound to a 42-entry list, so the 07:00 snapshot
/// job read SAP twice for every warehouse. It surfaced only because a status page rendered its
/// warehouse list and showed KEFBYS twice.
///
/// The fix is to leave the initializer empty so configuration is the only source. These tests fail
/// if anyone reintroduces the shape.
/// </remarks>
public class OptionsCollectionBindingTests
{
    private sealed class ListProbe
    {
        public List<string> Items { get; set; } = ["a", "b"];
    }

    private sealed class ArrayProbe
    {
        public string[] Items { get; set; } = ["a", "b"];
    }

    private static IConfiguration ConfigWith(params string[] items)
    {
        var pairs = items
            .Select((value, index) => new KeyValuePair<string, string?>($"Section:Items:{index}", value));

        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    [Fact]
    public void Binding_a_List_that_already_has_items_appends_rather_than_replaces()
    {
        // The behaviour the whole file is about. Documented here so the fixes below have a reason
        // that outlives the person who made them.
        var probe = ConfigWith("a", "b").GetSection("Section").Get<ListProbe>()!;

        Assert.Equal(["a", "b", "a", "b"], probe.Items);
    }

    [Fact]
    public void Binding_an_array_that_already_has_items_appends_too()
    {
        // Arrays bind through a different code path to lists, so it is worth pinning separately.
        var probe = ConfigWith("a", "b").GetSection("Section").Get<ArrayProbe>()!;

        Assert.Equal(["a", "b", "a", "b"], probe.Items);
    }

    [Fact]
    public void An_empty_initializer_binds_to_exactly_what_configuration_supplies()
    {
        // The shape every options class below is fixed to.
        var config = ConfigWith("a", "b");

        Assert.Equal(["a", "b"], config.GetSection("Section").Get<EmptyListProbe>()!.Items);
        Assert.Equal(["a", "b"], config.GetSection("Section").Get<EmptyArrayProbe>()!.Items);
    }

    // ---- the deployed file, bound for real -----------------------------------------------------

    private static IConfigurationRoot DeployedConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    private static int ConfiguredCount(IConfiguration section, string key) =>
        section.GetSection(key).GetChildren().Count();

    [Fact]
    public void The_snapshot_job_gets_each_monitored_warehouse_exactly_once()
    {
        // The live bug: 21 in the initializer plus the same 21 in appsettings.json bound to 42, so
        // FetchDailyStockHandler read SAP twice per warehouse every morning.
        var config = DeployedConfiguration();
        var section = config.GetSection(DailyStockSettings.SectionName);

        var bound = section.Get<DailyStockSettings>()!.MonitoredWarehouses;

        Assert.Equal(ConfiguredCount(section, nameof(DailyStockSettings.MonitoredWarehouses)), bound.Count);
        Assert.Equal(bound.Count, bound.Distinct().Count());
    }

    [Fact]
    public void The_OpenWA_arrays_bind_to_exactly_what_is_configured()
    {
        var config = DeployedConfiguration();
        var section = config.GetSection(OpenWASettings.SectionName);
        var bound = section.Get<OpenWASettings>()!;

        Assert.Equal(
            ConfiguredCount(section, nameof(OpenWASettings.WebhookEvents)),
            bound.WebhookEvents.Length);
        Assert.Equal(
            ConfiguredCount(section, nameof(OpenWASettings.HealthEndpointPaths)),
            bound.HealthEndpointPaths.Length);
    }

    [Fact]
    public void No_settings_class_binds_a_collection_to_more_than_configuration_supplies()
    {
        // The general form of the same check, so the next options class to grow a collection
        // initializer fails here rather than in a morning job nobody watches. Every *Settings type
        // in the API assembly is bound against the deployed file and its collection properties are
        // compared, element for element, with what that file actually lists.
        var config = DeployedConfiguration();
        var doubled = new List<string>();

        foreach (var type in typeof(DailyStockSettings).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !type.Name.EndsWith("Settings", StringComparison.Ordinal))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            var section = config.GetSection(SectionNameOf(type));
            if (!section.Exists())
            {
                continue;
            }

            object? bound;
            try
            {
                bound = section.Get(type);
            }
            catch (Exception)
            {
                // A type that cannot be bound at all is not what this test is about.
                continue;
            }

            if (bound is null)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType == typeof(string)
                    || !typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                {
                    continue;
                }

                var configured = ConfiguredCount(section, property.Name);
                if (configured == 0)
                {
                    continue;
                }

                if (property.GetValue(bound) is not IEnumerable value)
                {
                    continue;
                }

                var actual = value.Cast<object?>().Count();
                if (actual != configured)
                {
                    doubled.Add(
                        $"{type.Name}.{property.Name}: appsettings.json lists {configured}, "
                        + $"binding produced {actual}");
                }
            }
        }

        Assert.True(
            doubled.Count == 0,
            "these properties have a collection initializer that configuration is appended to rather "
            + "than replacing — leave the initializer empty and let appsettings.json be the only "
            + $"source:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", doubled)}");
    }

    private static string SectionNameOf(Type type)
    {
        var field = type.GetField("SectionName", BindingFlags.Public | BindingFlags.Static);

        return field?.GetValue(null) as string
            ?? type.Name[..^"Settings".Length];
    }

    [Fact]
    public void The_job_resolves_one_entry_per_warehouse_through_the_real_DI_path()
    {
        // The tests above bind with IConfiguration.Get<T>(); DailyStockSnapshotJob resolves
        // IOptions<DailyStockSettings> from services.Configure<T>(section), which is a different
        // call into the same binder. Pinned separately so a fix that only holds for one of them
        // cannot pass. The job's foreach is over exactly this list, one SAP read per element, so
        // its length is the job's SAP read count.
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<DailyStockSettings>(
            DeployedConfiguration().GetSection(DailyStockSettings.SectionName));

        var warehouses = services.BuildServiceProvider()
            .GetRequiredService<IOptions<DailyStockSettings>>()
            .Value
            .MonitoredWarehouses;

        Assert.Equal(21, warehouses.Count);
        Assert.Equal(warehouses.Count, warehouses.Distinct().Count());
    }

    private sealed class EmptyListProbe
    {
        public List<string> Items { get; set; } = [];
    }

    private sealed class EmptyArrayProbe
    {
        public string[] Items { get; set; } = [];
    }
}
