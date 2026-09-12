using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the daily stock snapshot being adjusted as transfers move stock between monitored
/// warehouses.
/// </summary>
/// <remarks>
/// These adjustments were being computed and then thrown away: the handler read the snapshot rows,
/// changed them and saved, but the context was registered to return untracked entities, so
/// SaveChangesAsync had nothing to write and said nothing about it. The snapshot silently drifted
/// from the stock it was supposed to describe. Every assertion here reads the rows back out of
/// storage rather than trusting the in-memory objects, because trusting them is what hid it.
/// </remarks>
public sealed class DailyStockAdjustmentTests : IDisposable
{
    private const string Source = "WH01";
    private const string Destination = "WH02";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>
    /// One settings instance for the handler and for the rows the tests plant, so both date a
    /// snapshot the same way. They used to disagree: the rows were dated <c>DateTime.UtcNow.Date</c>
    /// while the handler resolves the day in force from the fetch time, and the two answers differ
    /// between 02:00 and 07:00 CAT — so every assertion here would have failed for five hours a day.
    /// </summary>
    private DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = [Source, Destination]
    };

    public DailyStockAdjustmentTests()
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
    public async Task Stock_leaving_a_monitored_warehouse_is_deducted_in_storage()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);

        var result = await Handler().Handle(Transfer("ITEM-1", 4m), default);

        Assert.False(result.IsError);
        Assert.Equal(6m, await AvailableAsync(Source, "ITEM-1"));
    }

    [Fact]
    public async Task Stock_arriving_in_a_monitored_warehouse_is_added_in_storage()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);
        await AddSnapshotItemAsync(Destination, "ITEM-1", 2m);

        await Handler().Handle(Transfer("ITEM-1", 4m), default);

        Assert.Equal(6m, await AvailableAsync(Source, "ITEM-1"));
        Assert.Equal(6m, await AvailableAsync(Destination, "ITEM-1"));
    }

    [Fact]
    public async Task An_outbound_transfer_draws_down_the_earliest_expiring_batch_first()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 3m, new DateTime(2026, 8, 1));
        await AddSnapshotItemAsync(Source, "ITEM-1", 5m, new DateTime(2026, 9, 1));

        await Handler().Handle(Transfer("ITEM-1", 4m), default);

        var rows = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(item => item.WarehouseCode == Source)
            .OrderBy(item => item.ExpiryDate)
            .ToListAsync();

        Assert.Equal(0m, rows[0].AvailableQuantity);
        Assert.Equal(4m, rows[1].AvailableQuantity);
    }

    [Fact]
    public async Task A_transfer_between_unmonitored_warehouses_changes_nothing()
    {
        await AddSnapshotItemAsync("WH99", "ITEM-1", 10m);

        var result = await Handler().Handle(
            new ProcessTransferEventCommand("ITEM-1", "WH98", "WH99", 4m, 1, 1), default);

        Assert.False(result.IsError);
        Assert.False(result.Value.Adjusted);
        Assert.Equal(10m, await AvailableAsync("WH99", "ITEM-1"));
    }

    /// <remarks>
    /// The fetch time is the lever rather than the clock, the way <c>LocalStockLedgerDayTests</c>
    /// pulls it: a fetch time of 23:59 puts any real test run before the morning boundary and 00:00
    /// puts all of it after, so both sides of the roll are assertable at whatever hour the suite
    /// happens to run. A test pinned to a literal date would instead start failing on its own — see
    /// <c>CaptureClockTests</c>.
    /// </remarks>
    [Fact]
    public async Task Before_the_fetch_time_a_transfer_adjusts_the_previous_days_snapshot()
    {
        _settings.StockFetchTimeCAT = "23:59";

        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);

        var result = await Handler().Handle(Transfer("ITEM-1", 4m), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.Adjusted);

        var row = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Include(item => item.Snapshot)
            .FirstAsync(item => item.WarehouseCode == Source && item.ItemCode == "ITEM-1");

        Assert.Equal(LedgerDay, row.Snapshot!.SnapshotDate);
        Assert.Equal(6m, row.AvailableQuantity);
    }

    [Fact]
    public async Task After_the_fetch_time_a_transfer_adjusts_todays_snapshot()
    {
        _settings.StockFetchTimeCAT = "00:00";

        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);

        var result = await Handler().Handle(Transfer("ITEM-1", 4m), default);

        Assert.False(result.IsError);
        Assert.Equal(6m, await AvailableAsync(Source, "ITEM-1"));
    }

    /// <summary>
    /// The negative control for the pair above: the handler must follow the configured boundary, not
    /// the UTC date. Dating the rows by the day in force and the handler by <c>UtcNow.Date</c> is
    /// precisely the bug, and it shows up as the adjustment landing on a day nothing reads.
    /// </summary>
    [Fact]
    public async Task A_transfer_never_writes_to_a_snapshot_day_the_till_is_not_reading()
    {
        _settings.StockFetchTimeCAT = "23:59";

        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);

        await Handler().Handle(Transfer("ITEM-1", 4m), default);

        var strayDays = await _context.StockTransferAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.SnapshotDate != LedgerDay)
            .Select(adjustment => adjustment.SnapshotDate)
            .Distinct()
            .ToListAsync();

        Assert.Empty(strayDays);
    }

    // ── Helpers ─────────────────────────────────────────

    private ProcessTransferEventHandler Handler() => new(
        _context,
        Options.Create(_settings),
        NullLogger<ProcessTransferEventHandler>.Instance);

    /// <summary>The snapshot day the handler will resolve, given the fetch time under test.</summary>
    private DateTime LedgerDay => StockLedgerDay.Today(_settings.StockFetchTimeCAT);

    private static ProcessTransferEventCommand Transfer(string itemCode, decimal quantity)
        => new(itemCode, Source, Destination, quantity, 4001, 4001);

    private async Task<decimal> AvailableAsync(string warehouse, string itemCode)
        => await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(item => item.WarehouseCode == warehouse && item.ItemCode == itemCode)
            .SumAsync(item => item.AvailableQuantity);

    private async Task AddSnapshotItemAsync(
        string warehouse,
        string itemCode,
        decimal available,
        DateTime? expiryDate = null)
    {
        var today = LedgerDay;
        var snapshot = await _context.DailyStockSnapshots
            .FirstOrDefaultAsync(item => item.SnapshotDate == today && item.WarehouseCode == warehouse);
        if (snapshot is null)
        {
            snapshot = new DailyStockSnapshotEntity { SnapshotDate = today, WarehouseCode = warehouse };
            _context.DailyStockSnapshots.Add(snapshot);
            await _context.SaveChangesAsync();
        }

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = itemCode,
            WarehouseCode = warehouse,
            OriginalQuantity = available,
            AvailableQuantity = available,
            ExpiryDate = expiryDate
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

}
