using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.Maintenance;

namespace ShopInventory.Tests;

/// <summary>
/// The maintenance switch as it is stored and served.
///
/// Two things have to hold. The switch must outlive the request that threw it and reach the other
/// API nodes, which is why it is a <c>SystemConfigs</c> row rather than a field. And reading it must
/// never be the thing that fails: it sits on the path of every request, during maintenance, on a
/// system whose database may be exactly what is being worked on.
/// </summary>
public sealed class MaintenanceStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public MaintenanceStoreTests()
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

    private MaintenanceStore NewStore(TimeProvider? clock = null) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MaintenanceStore>.Instance,
            clock);

    private static MaintenanceState Lockout(
        MaintenanceScope scope = MaintenanceScope.Transactions,
        IReadOnlyList<string>? apps = null,
        IReadOnlyList<MaintenanceAudience>? audiences = null,
        DateTime? endsAtUtc = null) =>
        new(
            Enabled: true,
            Scope: scope,
            Audiences: audiences ?? [MaintenanceAudience.MobileApps],
            Message: "Down for the stock migration.",
            AppIds: apps ?? [],
            StartedAtUtc: new DateTime(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc),
            EndsAtUtc: endsAtUtc,
            UpdatedBy: "ngoni");

    [Fact]
    public void A_deployment_that_has_never_been_touched_is_not_locked_out()
    {
        Assert.False(NewStore().Current.Enabled);
    }

    [Fact]
    public async Task The_switch_outlives_the_request_that_threw_it()
    {
        // The whole reason this is a row and not a field. The node that serves the next request is
        // usually not the one the operator was talking to.
        await NewStore().UpdateAsync(Lockout());

        var anotherNode = NewStore();
        await anotherNode.ReloadAsync();

        Assert.True(anotherNode.Current.Enabled);
        Assert.Equal("Down for the stock migration.", anotherNode.Current.Message);
        Assert.Equal("ngoni", anotherNode.Current.UpdatedBy);
    }

    [Fact]
    public async Task Every_field_survives_the_round_trip()
    {
        var endsAt = new DateTime(2026, 9, 22, 22, 30, 0, DateTimeKind.Utc);
        await NewStore().UpdateAsync(Lockout(
            MaintenanceScope.All,
            ["kefalos-vansales"],
            [MaintenanceAudience.WebPortal, MaintenanceAudience.OtherClients],
            endsAt));

        var reloaded = NewStore();
        await reloaded.ReloadAsync();
        var state = reloaded.Current;

        Assert.Equal(MaintenanceScope.All, state.Scope);
        Assert.Equal(["kefalos-vansales"], state.AppIds);
        Assert.Equal([MaintenanceAudience.WebPortal, MaintenanceAudience.OtherClients], state.Audiences);
        Assert.Equal(endsAt, state.EndsAtUtc);
        Assert.Equal(DateTimeKind.Utc, state.EndsAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task A_lockout_stored_before_audiences_existed_still_stops_the_phones()
    {
        // The upgrade case, and the one worth a test: the deployment that brings audiences in is
        // quite likely to be the very thing the lockout was switched on for. Reading none of its
        // keys, finding it off and letting the vans straight back in would be the worst possible
        // moment to do it.
        await WriteLegacyRows(
            ("Mobile.Maintenance.Enabled", "true"),
            ("Mobile.Maintenance.Scope", "All"),
            ("Mobile.Maintenance.Message", "Down for the stock migration."),
            ("Mobile.Maintenance.Apps", "[\"kefalos-vansales\"]"),
            ("Mobile.Maintenance.UpdatedBy", "ngoni"));

        var store = NewStore();
        await store.ReloadAsync();
        var state = store.Current;

        Assert.True(state.Enabled);
        Assert.Equal(MaintenanceScope.All, state.Scope);
        Assert.Equal("Down for the stock migration.", state.Message);
        Assert.Equal(["kefalos-vansales"], state.AppIds);
        Assert.Equal("ngoni", state.UpdatedBy);

        // And it means what it meant: the phones, not everybody. Widening it on upgrade would turn
        // somebody's van lockout into a company-wide one without anybody asking for it.
        Assert.Equal([MaintenanceAudience.MobileApps], state.Audiences);
    }

    [Fact]
    public async Task A_new_row_wins_over_the_name_the_key_used_to_have()
    {
        // Both spellings can be present at once, because the legacy rows are read and never
        // written — nothing deletes them. The current one has to be the answer, or the first save
        // after an upgrade would appear not to take.
        await WriteLegacyRows(("Mobile.Maintenance.Enabled", "true"));

        var store = NewStore();
        await store.UpdateAsync(MaintenanceState.Off);

        var reloaded = NewStore();
        await reloaded.ReloadAsync();

        Assert.False(reloaded.Current.Enabled);
    }

    [Fact]
    public async Task An_audiences_row_naming_nothing_this_build_knows_falls_back_to_the_phones()
    {
        await WriteLegacyRows(
            ("Maintenance.Enabled", "true"),
            ("Maintenance.Audiences", "[\"Telepathy\"]"));

        var store = NewStore();
        await store.ReloadAsync();

        Assert.Equal([MaintenanceAudience.MobileApps], store.Current.Audiences);
    }

    [Fact]
    public async Task An_audiences_row_that_is_not_json_falls_back_to_the_phones()
    {
        await WriteLegacyRows(
            ("Maintenance.Enabled", "true"),
            ("Maintenance.Audiences", "MobileApps, WebPortal"));

        var store = NewStore();
        await store.ReloadAsync();

        Assert.Equal([MaintenanceAudience.MobileApps], store.Current.Audiences);
    }

    /// <summary>
    /// Writes SystemConfigs rows straight, for the states only a hand-edit or an older build
    /// produces.
    /// </summary>
    private async Task WriteLegacyRows(params (string Key, string Value)[] rows)
    {
        await using var context = NewContext();
        foreach (var (key, value) in rows)
        {
            context.SystemConfigs.Add(new ShopInventory.Models.Entities.SystemConfigEntity
            {
                Key = key,
                Value = value,
                ValueType = "string",
                Category = "Maintenance",
                IsEditable = true,
                UpdatedAt = new DateTime(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc)
            });
        }

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Turning_it_off_is_stored_as_off_rather_than_left_behind()
    {
        var store = NewStore();
        await store.UpdateAsync(Lockout());
        await store.UpdateAsync(MaintenanceState.Off);

        var reloaded = NewStore();
        await reloaded.ReloadAsync();

        Assert.False(reloaded.Current.Enabled);
        Assert.Null(reloaded.Current.EndsAtUtc);
    }

    [Fact]
    public async Task The_operator_who_threw_the_switch_sees_it_hold_immediately()
    {
        // Without waiting out the refresh interval on the node they happen to be talking to.
        var store = NewStore();
        await store.UpdateAsync(Lockout());

        Assert.True(store.Current.Enabled);
    }

    [Fact]
    public async Task Another_node_picks_the_change_up_without_being_asked()
    {
        // ReloadAsync is not the live path; Current is. This is the mechanism by which the other
        // API nodes converge on a switch thrown on one of them, with nobody restarting anything.
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var node = NewStore(clock);
        await node.ReloadAsync();

        await NewStore().UpdateAsync(Lockout());

        // Still serving the snapshot it loaded a moment ago.
        Assert.False(node.Current.Enabled);

        await SettleAsync(node, clock);

        Assert.True(node.Current.Enabled);
    }

    [Fact]
    public async Task A_stored_scope_that_is_nonsense_falls_back_to_transactions_rather_than_failing()
    {
        // A bad row must not take the API down, and must not silently become the strictest lockout
        // either — Transactions is what an operator who typed something wrong most likely meant.
        await NewStore().UpdateAsync(Lockout(MaintenanceScope.All));
        await PoisonAsync(MaintenanceStore.ScopeKey, "Everything");

        var store = NewStore();
        await store.ReloadAsync();

        Assert.Equal(MaintenanceScope.Transactions, store.Current.Scope);
        Assert.True(store.Current.Enabled);
    }

    [Fact]
    public async Task A_stored_enabled_flag_that_is_nonsense_reads_as_off()
    {
        await NewStore().UpdateAsync(Lockout());
        await PoisonAsync(MaintenanceStore.EnabledKey, "perhaps");

        var store = NewStore();
        await store.ReloadAsync();

        Assert.False(store.Current.Enabled);
    }

    [Fact]
    public async Task An_app_key_that_no_longer_exists_widens_the_lockout_rather_than_narrowing_it_to_nothing()
    {
        // A key that cannot match would read on the screen as "maintenance is on" while every
        // handset kept trading. Dropping it leaves the list empty, and empty covers everything.
        await NewStore().UpdateAsync(Lockout(apps: ["kefalos-vansales"]));
        await PoisonAsync(MaintenanceStore.AppsKey, """["app-that-was-retired"]""");

        var store = NewStore();
        await store.ReloadAsync();

        Assert.Empty(store.Current.AppIds);
        Assert.True(store.Current.CoversApp("cheeseman-driver"));
    }

    [Fact]
    public async Task A_stored_end_time_that_is_nonsense_leaves_the_lockout_open_ended()
    {
        await NewStore().UpdateAsync(Lockout(endsAtUtc: new DateTime(2026, 9, 22, 22, 0, 0, DateTimeKind.Utc)));
        await PoisonAsync(MaintenanceStore.EndsAtKey, "half past eight");

        var store = NewStore();
        await store.ReloadAsync();

        Assert.Null(store.Current.EndsAtUtc);
        Assert.True(store.Current.IsActiveAt(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task The_seeded_rows_describe_a_switch_that_is_off()
    {
        // An operator cannot edit a row that does not exist, and the row that exists must not be
        // one that locks anybody out.
        var rows = MaintenanceStore.DescribeDefaultRows(new DateTime(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc));

        Assert.Equal("false", rows.Single(row => row.Key == MaintenanceStore.EnabledKey).Value);
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Description)));

        await Task.CompletedTask;
    }

    /// <summary>Write a value straight into the row, the way a mistyped edit would.</summary>
    private async Task PoisonAsync(string key, string value)
    {
        await using var context = NewContext();
        var row = await context.SystemConfigs.SingleAsync(config => config.Key == key);
        row.Value = value;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Wait for a background refresh to land. Bounded by real time only so that a failure fails
    /// rather than hangs; what the test asserts on is the value, not how long it took.
    /// </summary>
    /// <remarks>
    /// Time is advanced in a loop rather than once, because a refresh already in flight when the
    /// update landed reads the old value and stamps itself fresh — so picking the change up can
    /// take another interval. Same reasoning as <c>RateLimitConfigStoreTests.SettleAsync</c>.
    /// </remarks>
    private static async Task SettleAsync(MaintenanceStore store, TestClock clock)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!store.Current.Enabled && DateTime.UtcNow < deadline)
        {
            clock.Advance(MaintenanceStore.RefreshInterval);
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// The application's model over SQLite.
    /// </summary>
    /// <remarks>
    /// <c>DailyStockSnapshotItem.Version</c> is a store-generated concurrency token mapped to
    /// Postgres' <c>xmin</c>. SQLite has no equivalent, so EF leaves it out of the INSERT and the
    /// NOT NULL constraint fails when the schema is created. Nothing here touches that entity.
    /// Same shim as <c>RateLimitConfigStoreTests</c>.
    /// </remarks>
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

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
