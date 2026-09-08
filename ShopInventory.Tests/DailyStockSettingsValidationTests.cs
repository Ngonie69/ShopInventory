using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Checks that an empty <c>DailyStock:MonitoredWarehouses</c> stops the application starting.
/// </summary>
/// <remarks>
/// This is the hole the double-binding fix opened. The list used to carry all 21 warehouses as a
/// collection initializer, so configuration could never leave it empty — at the cost of binding to
/// 42, since the binder appends rather than replaces ([[OptionsCollectionBindingTests]] pins that).
/// Taking the initializer out fixes the doubling and means a missing or misspelled key now binds to
/// nothing, which every consumer treats as a legitimate answer.
///
/// Left unguarded that is worse than a crash: <c>FetchDailyStockHandler</c> loops over the list, so
/// an empty one snapshots no warehouses and reports success having done nothing. The snapshot rows
/// are simply absent, and every till then validates against nothing and refuses every line —
/// silently, until a cashier tries to sell.
///
/// So these tests drive the real host through <c>ValidateOnStart</c> rather than calling
/// <see cref="DailyStockSettingsValidation.Validate"/> directly: what matters is that startup stops,
/// not that a method returns Fail.
/// </remarks>
public class DailyStockSettingsValidationTests
{
    /// <summary>
    /// Registers the options exactly as Program.cs does, over a supplied configuration.
    /// </summary>
    private static IHost HostWith(params KeyValuePair<string, string?>[] settings)
    {
        return new HostBuilder()
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                services.AddOptions<DailyStockSettings>()
                    .Bind(context.Configuration.GetSection("DailyStock"))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<DailyStockSettings>, DailyStockSettingsValidation>();
            })
            .Build();
    }

    private static KeyValuePair<string, string?>[] Warehouses(params string[] codes) =>
        codes
            .Select((code, index) =>
                new KeyValuePair<string, string?>($"DailyStock:MonitoredWarehouses:{index}", code))
            .ToArray();

    [Fact]
    public async Task A_missing_section_stops_the_application_starting()
    {
        // The misspelled- or dropped-key case. Nothing supplies MonitoredWarehouses at all.
        using var host = HostWith();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains("DailyStock:MonitoredWarehouses is empty", failure.Message);
    }

    [Fact]
    public async Task An_explicitly_empty_list_stops_the_application_starting()
    {
        using var host = HostWith(
            new KeyValuePair<string, string?>("DailyStock:StockFetchTimeCAT", "07:00"));

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains("refuse every sale", failure.Message);
    }

    [Fact]
    public async Task A_duplicated_warehouse_stops_the_application_starting()
    {
        // The original bug's signature. If the list ever holds a warehouse twice again — whether
        // from a reintroduced default or a hand-edited file — this is where it surfaces, rather
        // than as a doubled morning job nobody is watching.
        using var host = HostWith(Warehouses("KEFSHOP", "KEFBYS", "KEFSHOP"));

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains("KEFSHOP", failure.Message);
        Assert.DoesNotContain("KEFBYS", failure.Message);
    }

    [Fact]
    public async Task Case_and_whitespace_do_not_hide_a_duplicate()
    {
        using var host = HostWith(Warehouses("KEFSHOP", " kefshop "));

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains("more than once", failure.Message);
    }

    [Fact]
    public async Task A_normal_list_starts()
    {
        // The positive control. Without this the tests above would pass just as happily against a
        // validator that refused everything.
        using var host = HostWith(Warehouses("KEFSHOP", "KEFBYS", "KEFBYC"));

        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal(
            ["KEFSHOP", "KEFBYS", "KEFBYC"],
            host.Services.GetRequiredService<IOptions<DailyStockSettings>>().Value.MonitoredWarehouses);
    }

    [Fact]
    public async Task The_deployed_appsettings_starts()
    {
        // The one that would actually block a release: the file we ship has to satisfy the rule we
        // just added. Reads the same copy of appsettings.json the test project puts in its output.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddOptions<DailyStockSettings>()
                    .Bind(configuration.GetSection("DailyStock"))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<DailyStockSettings>, DailyStockSettingsValidation>();
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.NotEmpty(
            host.Services.GetRequiredService<IOptions<DailyStockSettings>>().Value.MonitoredWarehouses);
    }
}
