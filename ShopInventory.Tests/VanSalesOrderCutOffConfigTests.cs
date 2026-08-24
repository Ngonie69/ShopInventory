using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Reading the customer ordering rules out of <c>SystemConfigs</c>, where an operations manager can
/// change them without waiting for a release.
///
/// Everything here is about what happens when a row is wrong, because that is the only interesting
/// case. A settings row edited by hand can be blank, can be prose, can be a number someone typed an
/// extra zero onto — and none of those may take the ordering screen down. A cut-off that falls back
/// to a sane default is visible and correctable; an app that refuses every customer because a config
/// row says "sixteen hundred" looks like an outage and gets escalated as one.
///
/// The price list is the one to be most careful with: a bad value there would not fail loudly, it
/// would quote every customer the wrong money.
/// </summary>
public sealed class VanSalesOrderCutOffConfigTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesOrderCutOffConfigTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Unset_rules_fall_back_to_their_documented_defaults()
    {
        // The state every deployment starts in: nobody has created the rows yet.
        var rules = await ReadAsync();

        Assert.Equal(VanSalesOrderingPolicy.DefaultCutOffHours, rules.CutOffHoursBeforeVisitDay);
        Assert.Equal(VanSalesOrderingPolicy.DefaultPriceListNumber, rules.PriceListNumber);
        Assert.Equal(VanSalesOrderingPolicy.DefaultLowStockThreshold, rules.LowStockThreshold);
    }

    [Fact]
    public async Task Configured_rules_are_honoured()
    {
        await GivenConfiguredAsync(VanSalesOrderingPolicy.CutOffHoursConfigKey, "12");
        await GivenConfiguredAsync(VanSalesOrderingPolicy.PriceListConfigKey, "13");
        await GivenConfiguredAsync(VanSalesOrderingPolicy.LowStockThresholdConfigKey, "25.5");

        var rules = await ReadAsync();

        Assert.Equal(12, rules.CutOffHoursBeforeVisitDay);
        Assert.Equal(13, rules.PriceListNumber);
        Assert.Equal(25.5m, rules.LowStockThreshold);
    }

    [Fact]
    public async Task One_bad_row_does_not_spoil_the_others()
    {
        // They are read together, so a single unreadable row must not drag the rest to defaults.
        await GivenConfiguredAsync(VanSalesOrderingPolicy.CutOffHoursConfigKey, "not a number");
        await GivenConfiguredAsync(VanSalesOrderingPolicy.PriceListConfigKey, "13");

        var rules = await ReadAsync();

        Assert.Equal(VanSalesOrderingPolicy.DefaultCutOffHours, rules.CutOffHoursBeforeVisitDay);
        Assert.Equal(13, rules.PriceListNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sixteen hundred")]
    [InlineData("8.5")]
    public async Task A_cut_off_that_is_not_a_whole_number_falls_back(string value)
    {
        await GivenConfiguredAsync(VanSalesOrderingPolicy.CutOffHoursConfigKey, value);

        Assert.Equal(VanSalesOrderingPolicy.DefaultCutOffHours, (await ReadAsync()).CutOffHoursBeforeVisitDay);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("169")]
    public async Task A_cut_off_outside_the_sane_range_falls_back(string value)
    {
        // Negative would put the deadline after the van had left; more than a week would close
        // ordering before the previous call had even happened.
        await GivenConfiguredAsync(VanSalesOrderingPolicy.CutOffHoursConfigKey, value);

        Assert.Equal(VanSalesOrderingPolicy.DefaultCutOffHours, (await ReadAsync()).CutOffHoursBeforeVisitDay);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("168")]
    public async Task The_edges_of_the_cut_off_range_are_allowed(string value)
    {
        await GivenConfiguredAsync(VanSalesOrderingPolicy.CutOffHoursConfigKey, value);

        Assert.Equal(int.Parse(value), (await ReadAsync()).CutOffHoursBeforeVisitDay);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("list one")]
    public async Task A_price_list_that_is_not_a_positive_number_falls_back(string value)
    {
        // Price list 0 is not a list. Quoting from it would show every item as free.
        await GivenConfiguredAsync(VanSalesOrderingPolicy.PriceListConfigKey, value);

        Assert.Equal(VanSalesOrderingPolicy.DefaultPriceListNumber, (await ReadAsync()).PriceListNumber);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("plenty")]
    public async Task A_negative_or_unreadable_low_stock_threshold_falls_back(string value)
    {
        await GivenConfiguredAsync(VanSalesOrderingPolicy.LowStockThresholdConfigKey, value);

        Assert.Equal(VanSalesOrderingPolicy.DefaultLowStockThreshold, (await ReadAsync()).LowStockThreshold);
    }

    [Fact]
    public void The_seeded_rows_describe_themselves_for_whoever_edits_them()
    {
        // These descriptions are the only thing standing between an operator and guessing what a
        // number means. "8" on its own does not say eight hours before what.
        var rows = VanSalesOrderingPolicy.DescribeDefaultRows(
            new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsEditable));
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Description)));
        Assert.All(rows, row => Assert.Equal("VanSales", row.Category));

        var cutOff = rows.Single(r => r.Key == VanSalesOrderingPolicy.CutOffHoursConfigKey);
        Assert.Equal("8", cutOff.Value);
        Assert.Contains("16:00", cutOff.Description);
    }

    private async Task<VanSalesOrderingRules> ReadAsync() =>
        await new VanSalesOrderingPolicy(_context, NullLogger<VanSalesOrderingPolicy>.Instance)
            .GetRulesAsync(default);

    private async Task GivenConfiguredAsync(string key, string? value)
    {
        _context.SystemConfigs.Add(new SystemConfigEntity
        {
            Key = key,
            Value = value,
            ValueType = "string",
            Category = "VanSales"
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>
    /// <see cref="DailyStockSnapshotItemEntity.Version"/> is <c>[Timestamp]</c>, mapped by Npgsql to
    /// the store-generated <c>xmin</c>, which SQLite has no equivalent for. Nothing here touches it.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DailyStockSnapshotItemEntity>()
                .Property(item => item.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
