using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.RateLimit;

namespace ShopInventory.Tests;

/// <summary>
/// The rate limits an operator sets through <c>PUT /api/RateLimit/config</c>.
///
/// Before this store existed the endpoint answered "updated successfully" and changed nothing:
/// <c>IRateLimitService</c> is scoped and the limits were instance fields on it, so a write died
/// with the request that made it, and the ASP.NET Core limiter that actually returns 429 was
/// reading a separate copy captured at startup. Every test here is about the two halves of fixing
/// that - a change outliving its request, and a stored value that would break the limiter never
/// reaching it.
/// </summary>
public sealed class RateLimitConfigStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public RateLimitConfigStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using (var seed = NewContext())
        {
            seed.Database.EnsureCreated();
        }

        _provider = new ServiceCollection()
            .AddScoped<ApplicationDbContext>(_ => NewContext())
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private ApplicationDbContext NewContext() =>
        new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    /// <summary>A store as the running application builds it, over the same database.</summary>
    private RateLimitConfigStore NewStore(
        Dictionary<string, string?>? configuration = null,
        TimeProvider? clock = null) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(configuration ?? Configured()).Build(),
            NullLogger<RateLimitConfigStore>.Instance,
            clock);

    private static Dictionary<string, string?> Configured() => new()
    {
        ["RateLimit:PermitLimit"] = "100",
        ["RateLimit:WindowSeconds"] = "60",
        ["RateLimit:QueueLimit"] = "10",
        ["RateLimit:AuthEndpointPermitLimit"] = "10",
        ["RateLimit:AuthEndpointWindowSeconds"] = "60"
    };

    [Fact]
    public void With_nothing_stored_the_configured_limits_are_in_force()
    {
        // Every deployment starts here: no rows, so appsettings still decides.
        var current = NewStore().Current;

        Assert.Equal(100, current.PermitLimit);
        Assert.Equal(60, current.WindowSeconds);
        Assert.Equal(15, current.BlockDurationMinutes);   // documented default, absent from appsettings
    }

    [Fact]
    public async Task An_update_is_in_force_immediately_on_the_instance_that_made_it()
    {
        var store = NewStore();

        await store.UpdateAsync(Settings(permitLimit: 25, windowSeconds: 30));

        // Not "after the refresh interval": the operator who made the change is the one who checks.
        Assert.Equal(25, store.Current.PermitLimit);
        Assert.Equal(30, store.Current.WindowSeconds);
    }

    [Fact]
    public async Task An_update_outlives_the_process_that_made_it()
    {
        // The bug this whole change exists for. A second store over the same database stands in for
        // the next request, the next instance, and the next restart alike - none of which shared the
        // scoped service the old code wrote to.
        await NewStore().UpdateAsync(Settings(permitLimit: 7, windowSeconds: 90));

        var afterRestart = NewStore();
        await afterRestart.ReloadAsync();

        Assert.Equal(7, afterRestart.Current.PermitLimit);
        Assert.Equal(90, afterRestart.Current.WindowSeconds);
    }

    [Fact]
    public async Task A_stale_read_picks_the_change_up_without_being_asked()
    {
        // ReloadAsync is not the live path; Current is. This proves the background refresh that a
        // stale read starts actually lands - the mechanism by which a second instance converges
        // without anybody restarting it.
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var reader = NewStore(clock: clock);

        _ = reader.Current;                                   // first touch starts the initial load
        Assert.Equal(100, reader.Current.PermitLimit);        // the configured value, so far

        await NewStore().UpdateAsync(Settings(permitLimit: 42, windowSeconds: 60));

        // Let time pass the way it does in production. A refresh already in flight when the
        // update landed reads the old value and stamps itself fresh, so picking the change up
        // can take another interval - which is fine while the clock keeps moving, and is why
        // this advances rather than jumping once.
        await SettleAsync(reader, expected: 42, clock);

        Assert.Equal(42, reader.Current.PermitLimit);
    }

    /// <summary>
    /// Wait for a background refresh to land. Bounded by real time only to fail rather than hang;
    /// what the test asserts on is the value, not how long it took.
    /// </summary>
    private static async Task SettleAsync(RateLimitConfigStore store, int expected, TestClock clock)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (store.Current.PermitLimit != expected && DateTime.UtcNow < deadline)
        {
            clock.Advance(RateLimitConfigStore.RefreshInterval);
            await Task.Delay(25);
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    [Fact]
    public async Task Settings_the_endpoint_cannot_set_survive_an_update()
    {
        // RateLimitConfigDto carries no queue limit and no auth-endpoint limits. A write that never
        // mentioned them must not reset them to type defaults - that would quietly widen the
        // stricter limit protecting /api/auth/login.
        await NewStore().UpdateAsync(Settings(permitLimit: 55, windowSeconds: 60));

        var reloaded = NewStore();
        await reloaded.ReloadAsync();

        Assert.Equal(10, reloaded.Current.QueueLimit);
        Assert.Equal(10, reloaded.Current.AuthEndpointPermitLimit);
        Assert.Equal(60, reloaded.Current.AuthEndpointWindowSeconds);
    }

    [Theory]
    [InlineData("0")]        // ASP.NET Core throws building a limiter with no permits
    [InlineData("-5")]
    [InlineData("")]
    [InlineData("one hundred")]
    [InlineData("99999999999")]
    public async Task A_stored_permit_limit_that_would_break_the_limiter_is_ignored(string stored)
    {
        // The partition factory runs on every request, so a value that makes it throw is a total
        // outage that no restart clears. Falling back to the configured value keeps the API up and
        // leaves a warning behind.
        await GivenStoredAsync(RateLimitConfigStore.PermitLimitKey, stored);

        var store = NewStore();
        await store.ReloadAsync();

        Assert.Equal(100, store.Current.PermitLimit);
    }

    [Fact]
    public async Task A_stored_window_of_zero_is_ignored()
    {
        await GivenStoredAsync(RateLimitConfigStore.WindowSecondsKey, "0");

        var store = NewStore();
        await store.ReloadAsync();

        Assert.Equal(60, store.Current.WindowSeconds);
    }

    [Fact]
    public async Task Whitelists_round_trip()
    {
        await NewStore().UpdateAsync(Settings(
            permitLimit: 100,
            windowSeconds: 60,
            ipWhitelist: ["10.10.10.6", "127.0.0.1"],
            apiKeyWhitelist: ["desktop-till"]));

        var reloaded = NewStore();
        await reloaded.ReloadAsync();

        Assert.Equal(["10.10.10.6", "127.0.0.1"], reloaded.Current.IpWhitelist);
        Assert.Equal(["desktop-till"], reloaded.Current.ApiKeyWhitelist);
    }

    [Fact]
    public async Task A_whitelist_row_that_is_not_a_json_array_is_ignored()
    {
        await GivenStoredAsync(RateLimitConfigStore.IpWhitelistKey, "10.10.10.6, 127.0.0.1");

        var store = NewStore();
        await store.ReloadAsync();

        // Empty, not a single entry of nonsense: a whitelist that half-parses would exempt the
        // wrong caller, and exempting the wrong caller is the failure that does not announce itself.
        Assert.Empty(store.Current.IpWhitelist);
    }

    [Fact]
    public async Task Turning_ip_rate_limiting_off_is_stored()
    {
        await NewStore().UpdateAsync(Settings(permitLimit: 100, windowSeconds: 60, enableIpRateLimiting: false));

        var reloaded = NewStore();
        await reloaded.ReloadAsync();

        Assert.False(reloaded.Current.EnableIpRateLimiting);
    }

    [Fact]
    public async Task Updating_twice_rewrites_the_same_rows()
    {
        var store = NewStore();
        await store.UpdateAsync(Settings(permitLimit: 10, windowSeconds: 60));
        await store.UpdateAsync(Settings(permitLimit: 20, windowSeconds: 60));

        using var context = NewContext();
        var rows = await context.SystemConfigs
            .Where(config => config.Key == RateLimitConfigStore.PermitLimitKey)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal("20", rows[0].Value);
    }

    [Fact]
    public async Task The_api_key_whitelist_row_is_marked_sensitive()
    {
        // It names credentials. An operator screen listing SystemConfigs should not print them.
        await NewStore().UpdateAsync(Settings(permitLimit: 100, windowSeconds: 60, apiKeyWhitelist: ["secret-key"]));

        using var context = NewContext();
        var row = await context.SystemConfigs
            .SingleAsync(config => config.Key == RateLimitConfigStore.ApiKeyWhitelistKey);

        Assert.True(row.IsSensitive);
    }

    [Fact]
    public void Changing_a_limit_changes_the_partition_fingerprint()
    {
        // ASP.NET Core builds a partition's options once and then caches the limiter under its
        // key. The fingerprint goes in front of every partition key so that changed settings are a
        // different partition, built fresh. Without it, tightening a limit did not touch the client
        // already hammering the API - measured against the running app before this existed: the
        // same 25-request burst answered 3x200 + 15x429 both before and after the change landed.
        var before = Settings(permitLimit: 100, windowSeconds: 60);
        var after = Settings(permitLimit: 3, windowSeconds: 60);

        Assert.NotEqual(before.Fingerprint(), after.Fingerprint());
    }

    [Fact]
    public void Adding_a_whitelist_entry_changes_the_partition_fingerprint()
    {
        // The whitelist returns GetNoLimiter under the same key the limited partition uses, so
        // without a fingerprint change a newly whitelisted caller kept its old limiter.
        var before = Settings(permitLimit: 100, windowSeconds: 60);
        var after = Settings(permitLimit: 100, windowSeconds: 60, ipWhitelist: ["127.0.0.1"]);

        Assert.NotEqual(before.Fingerprint(), after.Fingerprint());
    }

    [Fact]
    public void Unchanged_settings_keep_the_same_fingerprint()
    {
        // The other half: an unchanged setting must not churn partitions, or every refresh would
        // hand every client a fresh window and the limit would never bite.
        var a = Settings(permitLimit: 100, windowSeconds: 60, ipWhitelist: ["10.0.0.1", "10.0.0.2"]);
        var b = Settings(permitLimit: 100, windowSeconds: 60, ipWhitelist: ["10.0.0.2", "10.0.0.1"]);

        Assert.Equal(a.Fingerprint(), b.Fingerprint());   // order of the list must not matter
    }

    private async Task GivenStoredAsync(string key, string value)
    {
        using var context = NewContext();
        context.SystemConfigs.Add(new ShopInventory.Models.Entities.SystemConfigEntity
        {
            Key = key,
            Value = value,
            ValueType = "string",
            Category = "RateLimit",
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static RateLimitSettings Settings(
        int permitLimit,
        int windowSeconds,
        bool enableIpRateLimiting = true,
        List<string>? ipWhitelist = null,
        List<string>? apiKeyWhitelist = null) => new()
        {
            PermitLimit = permitLimit,
            WindowSeconds = windowSeconds,
            QueueLimit = 10,
            AuthEndpointPermitLimit = 10,
            AuthEndpointWindowSeconds = 60,
            EnableIpRateLimiting = enableIpRateLimiting,
            IpWhitelist = ipWhitelist ?? [],
            ApiKeyWhitelist = apiKeyWhitelist ?? [],
            BlockDurationMinutes = 15
        };

    /// <summary>
    /// <c>DailyStockSnapshotItem.Version</c> is a store-generated concurrency token mapped to
    /// Postgres' <c>xmin</c>. SQLite has no equivalent, so EF leaves it out of the INSERT and the
    /// NOT NULL constraint fails when the schema is created. Nothing here touches that entity.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ShopInventory.Models.Entities.DailyStockSnapshotItemEntity>()
                .Property(item => item.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
