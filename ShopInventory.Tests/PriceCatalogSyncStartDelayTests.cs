using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// A full price catalog sync used to run the moment an API node started, into the catch-up that
/// follows a cutover. It now waits until every other startup trigger has had its turn.
/// </summary>
public sealed class PriceCatalogSyncStartDelayTests
{
    private const string PriceSyncTrigger = "price-catalog-sync-trigger";

    [Fact]
    public void The_first_price_sync_waits_half_an_hour_after_start()
    {
        var before = DateTimeOffset.UtcNow;
        var trigger = Assert.Single(DeclaredTriggers(), t => t.Key.Name == PriceSyncTrigger);

        Assert.InRange(trigger.StartTimeUtc, before.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
    }

    [Fact]
    public void The_price_sync_starts_after_every_other_startup_trigger()
    {
        var triggers = DeclaredTriggers(new() { ["DailyStock:EnableAutoStockFetch"] = "true" });
        var priceSync = Assert.Single(triggers, t => t.Key.Name == PriceSyncTrigger);

        // Cron triggers fire at a time of day, not relative to start, so they are not in the rush.
        var others = triggers
            .Where(t => t is ISimpleTrigger && t.Key.Name != PriceSyncTrigger)
            .ToList();

        Assert.NotEmpty(others);
        Assert.All(others, t => Assert.True(
            t.StartTimeUtc < priceSync.StartTimeUtc,
            $"{t.Key.Name} starts at {t.StartTimeUtc:HH:mm:ss}, not before the price sync at {priceSync.StartTimeUtc:HH:mm:ss}"));
    }

    [Fact]
    public void Zero_restores_syncing_at_start()
    {
        var trigger = Assert.Single(
            DeclaredTriggers(new() { ["SAP:InitialDelayMinutes"] = "0" }),
            t => t.Key.Name == PriceSyncTrigger);

        Assert.True(trigger.StartTimeUtc <= DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void The_default_is_thirty_minutes() =>
        Assert.Equal(30, new SAPSettings().InitialDelayMinutes);

    private static IReadOnlyList<ITrigger> DeclaredTriggers(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddShopInventoryQuartz(configuration, "Host=unused");

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<QuartzOptions>>().Value.Triggers.ToList();
    }
}
