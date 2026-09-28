using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The list of warehouses the daily snapshot covers, as saved from the Local stock page.
/// </summary>
/// <remarks>
/// appsettings.json decides until a list is saved, and must go on deciding when what was saved cannot
/// be used: an empty or unreadable saved list obeyed as written would snapshot nothing and leave every
/// till refusing every sale.
/// </remarks>
public sealed class MonitoredWarehouseListTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private readonly DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = ["KEFSHOP", "VAN001"]
    };

    public MonitoredWarehouseListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SnapshotSqliteContext(
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
    public async Task Configuration_decides_until_a_list_is_saved()
    {
        var state = await MonitoredWarehouseList.ReadStateAsync(_context, _settings);

        Assert.Equal(["KEFSHOP", "VAN001"], state.Warehouses);
        Assert.Null(state.UpdatedAtUtc);
    }

    [Fact]
    public async Task A_saved_list_replaces_the_configured_one()
    {
        await MonitoredWarehouseList.SaveAsync(_context, ["KEFSHOP", "VAN001", "VAN002"], Guid.NewGuid());

        var state = await MonitoredWarehouseList.ReadStateAsync(_context, _settings);

        Assert.Equal(["KEFSHOP", "VAN001", "VAN002"], state.Warehouses);
        Assert.NotNull(state.UpdatedAtUtc);
    }

    [Fact]
    public async Task Saving_again_updates_the_one_row()
    {
        await MonitoredWarehouseList.SaveAsync(_context, ["KEFSHOP", "VAN002"], null);
        await MonitoredWarehouseList.SaveAsync(_context, ["KEFSHOP"], null);

        Assert.Equal(["KEFSHOP"], await MonitoredWarehouseList.ReadAsync(_context, _settings));
        Assert.Equal(1, await _context.SystemConfigs.CountAsync(c => c.Key == MonitoredWarehouseList.ConfigKey));
    }

    [Fact]
    public async Task A_saved_list_is_trimmed_upper_cased_and_listed_once()
    {
        var saved = await MonitoredWarehouseList.SaveAsync(_context, [" van002 ", "VAN002", "", "kefshop"], null);

        Assert.Equal(["KEFSHOP", "VAN002"], saved.Warehouses);
        Assert.Equal(["KEFSHOP", "VAN002"], await MonitoredWarehouseList.ReadAsync(_context, _settings));
    }

    [Fact]
    public async Task An_empty_list_is_refused_rather_than_saved()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => MonitoredWarehouseList.SaveAsync(_context, ["  "], null));

        Assert.False(await _context.SystemConfigs.AnyAsync(c => c.Key == MonitoredWarehouseList.ConfigKey));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_saved_list_that_cannot_be_used_falls_back_to_configuration(string value)
    {
        _context.SystemConfigs.Add(new SystemConfigEntity
        {
            Key = MonitoredWarehouseList.ConfigKey,
            Value = value,
            ValueType = "json"
        });
        await _context.SaveChangesAsync();

        Assert.Equal(["KEFSHOP", "VAN001"], await MonitoredWarehouseList.ReadAsync(_context, _settings));
    }
}
