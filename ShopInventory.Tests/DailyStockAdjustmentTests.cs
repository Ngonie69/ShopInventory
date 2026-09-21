using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;
using ShopInventory.Features.DesktopIntegration.Events.StockTransferReceived;
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
    private readonly CapturingPublisher _publisher = new();

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

    /// <summary>
    /// ICC011 at a shop, 2026-09-19: both batches had sold down to zero, a transfer brought 37 in,
    /// and the handler wrote them to a new row with no batch and no name. The till showed the item
    /// as "N/A" and nothing reconciled it, because the total then agreed with SAP.
    /// </summary>
    [Fact]
    public async Task An_arrival_for_a_sold_out_batched_item_lands_on_a_batch_it_already_has()
    {
        await AddSnapshotItemAsync(Destination, "ICC011", 0m, new DateTime(2026, 8, 1), "IC011#M24/1B-24");
        await AddSnapshotItemAsync(Destination, "ICC011", 0m, new DateTime(2026, 10, 1), "ICC011#E21/1,2,3,4 I-24");

        await Handler().Handle(Transfer("ICC011", 37m), default);

        var rows = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(item => item.WarehouseCode == Destination && item.ItemCode == "ICC011")
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, row => row.BatchNumber == null);
        Assert.Equal(37m, rows.Single(row => row.BatchNumber == "ICC011#E21/1,2,3,4 I-24").AvailableQuantity);
    }

    [Fact]
    public async Task A_row_created_for_a_new_item_carries_the_item_name_from_the_event()
    {
        // Only for the snapshot header: the handler writes into a day that was fetched, never one it invents.
        await AddSnapshotItemAsync(Destination, "ITEM-1", 1m);

        await Handler().Handle(
            new ProcessTransferEventCommand("ITEM-9", Source, Destination, 5m, 4002, 4002, "Feta 200g"),
            default);

        var row = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .SingleAsync(item => item.WarehouseCode == Destination && item.ItemCode == "ITEM-9");

        Assert.Equal("Feta 200g", row.ItemDescription);
        Assert.Equal(5m, row.AvailableQuantity);
    }

    // ── Helpers ─────────────────────────────────────────

    // ── what the webhook announces ─────────────────────────────────────────

    /// <summary>
    /// The one thing a van handset cannot find out on its own is that a load has landed after its
    /// morning position was taken. The webhook is where this system first hears of it, so the webhook
    /// is what has to say so — and only once the ledger row is saved, because the event is read as
    /// "there is stock to go and look at".
    /// </summary>
    [Fact]
    public async Task An_inbound_line_is_announced_once_it_is_on_the_ledger()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);
        await AddSnapshotItemAsync(Destination, "ITEM-1", 2m);

        await Handler().Handle(
            new ProcessTransferEventCommand("ITEM-1", Source, Destination, 4m, 7001, 9001, "Gouda 1kg"),
            default);

        var arrived = Assert.Single(_publisher.Of<StockTransferReceivedEvent>());
        Assert.Equal(Destination, arrived.WarehouseCode);
        Assert.Equal(Source, arrived.SourceWarehouse);
        Assert.Equal("ITEM-1", arrived.ItemCode);
        Assert.Equal("Gouda 1kg", arrived.ItemDescription);
        Assert.Equal(4m, arrived.Quantity);
        Assert.Equal(7001, arrived.TransferDocEntry);
        Assert.Equal(9001, arrived.TransferDocNum);
    }

    /// <summary>
    /// Stock leaving a monitored warehouse for one nobody here watches is not an arrival. Announcing it
    /// would wake the handsets of whoever drives the source, to go and read a figure that went down.
    /// </summary>
    [Fact]
    public async Task The_outbound_side_alone_announces_nothing()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);

        await Handler().Handle(
            new ProcessTransferEventCommand("ITEM-1", Source, "WH99", 4m, 7002, 9002), default);

        Assert.Empty(_publisher.Of<StockTransferReceivedEvent>());
    }

    /// <summary>
    /// The listener re-sends a line whose delivery it saw fail. The ledger already absorbs that as a
    /// duplicate; the announcement has to as well, or a handset refreshes twice for one load — and the
    /// second refresh is a full catalogue walk against SAP for nothing.
    /// </summary>
    [Fact]
    public async Task A_re_delivered_line_is_announced_only_the_first_time()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);
        await AddSnapshotItemAsync(Destination, "ITEM-1", 2m);

        var line = new ProcessTransferEventCommand("ITEM-1", Source, Destination, 4m, 7003, 9003);

        await Handler().Handle(line, default);
        await Handler().Handle(line, default);

        Assert.Single(_publisher.Of<StockTransferReceivedEvent>());
    }

    /// <summary>
    /// A van loaded before its day has a snapshot has no row for the arrival to land on. The stock is
    /// on the van all the same, and its adjustment row is written all the same — so the handset is
    /// told. Leaving this out is how a load booked at 06:50 stayed invisible until pull-to-refresh.
    /// </summary>
    [Fact]
    public async Task An_arrival_with_no_snapshot_for_the_day_is_still_recorded_and_announced()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);

        var result = await Handler().Handle(
            new ProcessTransferEventCommand("ITEM-1", Source, Destination, 4m, 7004, 9004), default);

        Assert.False(result.IsError);

        var recorded = await _context.StockTransferAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.WarehouseCode == Destination && adjustment.Direction == "IN")
            .ToListAsync();

        Assert.Single(recorded);
        Assert.Single(_publisher.Of<StockTransferReceivedEvent>());
    }

    private ProcessTransferEventHandler Handler() => new(
        _context,
        Options.Create(_settings),
        _publisher,
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
        DateTime? expiryDate = null,
        string? batchNumber = null)
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
            BatchNumber = batchNumber,
            ExpiryDate = expiryDate
        }.Opened(available));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

}
