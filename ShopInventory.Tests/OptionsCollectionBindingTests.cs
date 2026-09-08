using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Guards every options class against binding to its default *plus* its configured value.
/// </summary>
/// <remarks>
/// The configuration binder appends to a collection that already holds items rather than replacing
/// it — for <c>List&lt;T&gt;</c> and <c>T[]</c> alike. So an options property declared with a
/// non-empty collection initializer that is also supplied in appsettings.json silently holds both.
///
/// <c>DailyStockSettings.MonitoredWarehouses</c> was one, and it was expensive: 21 in the
/// initializer and the same 21 in appsettings.json bound to 42, so the 07:00 snapshot job walked
/// every warehouse twice and made double the SAP reads it needed, against a pool of six.
///
/// This is written as a sweep rather than one test per settings class because the mistake is a shape,
/// not a place — it is invisible at the declaration, and the next person to add a defaulted list will
/// not have read any of this. It reflects over every <c>*Settings</c> type in the API assembly and
/// binds each against the deployed appsettings.json, so a new one is covered without being listed.
/// </remarks>
public sealed class OptionsCollectionBindingTests
{
    [Fact]
    public void No_settings_collection_binds_to_more_than_the_file_lists()
    {
        var configuration = LoadDeployedConfiguration();
        var problems = new List<string>();

        foreach (var settingsType in SettingsTypes())
        {
            var section = SectionFor(settingsType, configuration);
            if (section is null)
            {
                continue;
            }

            var instance = Activator.CreateInstance(settingsType);
            if (instance is null)
            {
                continue;
            }

            section.Bind(instance);

            foreach (var property in settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsBindableCollection(property.PropertyType))
                {
                    continue;
                }

                var configured = section.GetSection(property.Name).GetChildren().Count();
                if (configured == 0)
                {
                    continue;  // Not supplied, so nothing was appended to.
                }

                var bound = property.GetValue(instance) is IEnumerable values
                    ? values.Cast<object?>().Count()
                    : 0;

                if (bound > configured)
                {
                    problems.Add(
                        $"{settingsType.Name}.{property.Name}: appsettings.json lists {configured}, "
                        + $"bound to {bound}. Its declaration has a collection initializer, which the "
                        + "binder appends to rather than replacing. Declare it as `= []`.");
                }
            }
        }

        Assert.Empty(problems);
    }

    /// <summary>
    /// The negative control. Without it, this suite would pass just as happily if the reflection
    /// found nothing at all, the section lookup always missed, or the comparison never fired.
    /// </summary>
    [Fact]
    public void The_sweep_detects_the_shape_it_is_looking_for()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Probe:Values:0"] = "x",
                ["Probe:Values:1"] = "y"
            })
            .Build();

        var probe = new DefaultedProbe();
        configuration.GetSection("Probe").Bind(probe);

        var configured = configuration.GetSection("Probe:Values").GetChildren().Count();

        Assert.Equal(2, configured);
        Assert.True(probe.Values.Count > configured,
            "The binder no longer appends. That is a welcome change — read every settings class "
            + "before deleting these tests, because their `= []` declarations were written for it.");
    }

    private sealed class DefaultedProbe
    {
        public List<string> Values { get; set; } = ["a", "b", "c"];
    }

    // ── Helpers ─────────────────────────────────────────

    /// <summary>
    /// The API's own appsettings.json, which is what production binds. Located by walking up from the
    /// test binaries rather than copied into this project, so it cannot drift from the real file.
    /// </summary>
    private static IConfigurationRoot LoadDeployedConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "ShopInventory", "appsettings.json");
            if (File.Exists(candidate))
            {
                return new ConfigurationBuilder().AddJsonFile(candidate, optional: false).Build();
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find ShopInventory/appsettings.json above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<Type> SettingsTypes() =>
        typeof(DailyStockSettings).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .Where(type => type.Name.EndsWith("Settings", StringComparison.Ordinal))
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null);

    /// <summary>
    /// The configuration section a settings class binds to: its <c>SectionName</c> constant where it
    /// has one, otherwise its name without the "Settings" suffix. Returns null when the file carries
    /// no such section, which is the common case and not a failure.
    /// </summary>
    private static IConfigurationSection? SectionFor(Type settingsType, IConfigurationRoot configuration)
    {
        var declared = settingsType
            .GetField("SectionName", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            ?.GetValue(null) as string;

        var names = new[]
        {
            declared,
            settingsType.Name.Replace("Settings", string.Empty, StringComparison.Ordinal)
        };

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var section = configuration.GetSection(name);
            if (section.Exists())
            {
                return section;
            }
        }

        return null;
    }

    /// <summary>
    /// Collections the binder populates. Strings are enumerable and must not be counted as one.
    /// </summary>
    private static bool IsBindableCollection(Type type) =>
        type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
}
