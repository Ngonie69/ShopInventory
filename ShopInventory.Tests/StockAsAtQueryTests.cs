using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Stock.Queries.GetStockAsAt;
using ShopInventory.Features.Stock.Queries.GetStockMovements;
using ShopInventory.Features.Stock.Queries.GetStockMovementTotals;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A shop's stock in hand at a past moment, rebuilt from the morning snapshot and the ledger's
/// journal, and the journal itself for one item.
/// </summary>
/// <remarks>
/// SAP's warehouse journal is not readable through SQLQueries, so this is the only way the stock
/// in hand page can answer "what did we hold at 10:00 yesterday".
/// </remarks>
public sealed class StockAsAtQueryTests : IDisposable
{
    private const string Shop = "KEFSHOP";

    // CAT is UTC+2 and the snapshot day rolls at 07:00 CAT (05:00 UTC).
    private static readonly DateTime Tuesday = new(2026, 11, 17);
    private static readonly DateTime Wednesday = new(2026, 11, 18);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly IOptions<DailyStockSettings> _settings =
        Options.Create(new DailyStockSettings { StockFetchTimeCAT = "07:00" });
    private readonly FixedClock _clock = new(Utc(2026, 11, 18, 9, 0));

    public StockAsAtQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task An_item_nothing_has_moved_holds_its_opening_summed_across_batches()
    {
        await AddSnapshot(Tuesday, ("FET-400", "Feta 400g", 7m), ("FET-400", "Feta 400g", 5m));

        var result = await AsAt(Utc(2026, 11, 17, 10, 0));

        Assert.True(result.Recorded);
        Assert.Equal(Tuesday, result.LedgerDay);
        var item = Assert.Single(result.Items);
        Assert.Equal("Feta 400g", item.ItemName);
        Assert.Equal(12m, item.OnHand);
        Assert.Equal(12m, item.OpeningQuantity);
        Assert.Null(item.LastMovementAtUtc);
    }

    [Fact]
    public async Task The_moment_takes_the_balance_of_the_last_movement_at_or_before_it_and_ignores_later_ones()
    {
        await AddSnapshot(Tuesday, ("YOG-1K", "Greek Yoghurt 1kg", 20m));
        await AddMovement(Tuesday, StockMovementKinds.Commit, "YOG-1K", -3m, 17m, Utc(2026, 11, 17, 7, 0));
        await AddMovement(Tuesday, StockMovementKinds.Transfer, "YOG-1K", 10m, 27m, Utc(2026, 11, 17, 8, 0));
        await AddMovement(Tuesday, StockMovementKinds.Commit, "YOG-1K", -6m, 21m, Utc(2026, 11, 17, 12, 0));

        var item = Assert.Single((await AsAt(Utc(2026, 11, 17, 8, 0))).Items);

        Assert.Equal(27m, item.OnHand);
        Assert.Equal(Utc(2026, 11, 17, 8, 0), item.LastMovementAtUtc);
        Assert.Equal(DateTimeKind.Utc, item.LastMovementAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task Before_the_morning_fetch_the_moment_belongs_to_the_previous_day_and_reads_its_close()
    {
        await AddSnapshot(Tuesday, ("HAL-250", "Halloumi 250g", 10m));
        await AddMovement(Tuesday, StockMovementKinds.Commit, "HAL-250", -4m, 6m, Utc(2026, 11, 17, 14, 0));
        await AddSnapshot(Wednesday, ("HAL-250", "Halloumi 250g", 6m));
        await AddMovement(Wednesday, StockMovementKinds.Commit, "HAL-250", -2m, 4m, Utc(2026, 11, 18, 6, 0));

        // 06:30 CAT on Wednesday is still Tuesday's ledger day.
        var result = await AsAt(Utc(2026, 11, 18, 4, 30));

        Assert.Equal(Tuesday, result.LedgerDay);
        Assert.Equal(6m, Assert.Single(result.Items).OnHand);
    }

    [Fact]
    public async Task An_item_a_movement_brought_in_after_the_fetch_is_listed_with_an_opening_of_nothing()
    {
        await AddSnapshot(Tuesday, ("FET-400", "Feta 400g", 1m));
        await AddMovement(Tuesday, StockMovementKinds.Transfer, "ICE-2L", 12m, 12m, Utc(2026, 11, 17, 9, 0));

        var result = await AsAt(Utc(2026, 11, 17, 10, 0));

        var ice = Assert.Single(result.Items, i => i.ItemCode == "ICE-2L");
        Assert.Equal(12m, ice.OnHand);
        Assert.Equal(0m, ice.OpeningQuantity);
    }

    [Fact]
    public async Task A_moment_no_finished_snapshot_covers_is_reported_as_not_recorded()
    {
        await AddSnapshot(Tuesday, ("FET-400", "Feta 400g", 1m));
        await AddMovement(Tuesday, StockMovementKinds.Commit, "FET-400", -1m, 0m, Utc(2026, 11, 17, 9, 0));

        var result = await AsAt(Utc(2026, 11, 10, 10, 0));

        Assert.False(result.Recorded);
        Assert.Empty(result.Items);
        Assert.Equal(Tuesday, result.RecordedFrom);
    }

    [Fact]
    public async Task A_snapshot_two_days_old_does_not_stand_in_for_a_day_that_was_never_fetched()
    {
        await AddSnapshot(Tuesday, ("FET-400", "Feta 400g", 1m));

        var result = await AsAt(Utc(2026, 11, 19, 8, 0), clock: new FixedClock(Utc(2026, 11, 19, 9, 0)));

        Assert.False(result.Recorded);
    }

    [Fact]
    public async Task Only_this_warehouses_rows_are_read()
    {
        await AddSnapshot(Tuesday, ("FET-400", "Feta 400g", 5m));
        await AddSnapshot(Tuesday, "KEFGRS", ("FET-400", "Feta 400g", 50m));
        await AddMovement(Tuesday, StockMovementKinds.Commit, "FET-400", -9m, 41m, Utc(2026, 11, 17, 7, 0), "KEFGRS");

        Assert.Equal(5m, Assert.Single((await AsAt(Utc(2026, 11, 17, 10, 0))).Items).OnHand);
    }

    [Fact]
    public async Task A_moment_in_the_future_is_read_as_now()
    {
        await AddSnapshot(Wednesday, ("FET-400", "Feta 400g", 5m));

        var result = await AsAt(Utc(2026, 11, 25, 10, 0));

        Assert.Equal(Utc(2026, 11, 18, 9, 0), result.AsAtUtc);
        Assert.True(result.Recorded);
    }

    [Fact]
    public async Task One_items_movements_come_oldest_first_with_each_morning_opening_inside_the_window()
    {
        await AddSnapshot(Tuesday, ("YOG-1K", "Greek Yoghurt 1kg", 20m));
        await AddMovement(Tuesday, StockMovementKinds.Commit, "YOG-1K", -3m, 17m, Utc(2026, 11, 17, 12, 0));
        await AddMovement(Tuesday, StockMovementKinds.Commit, "FET-400", -1m, 4m, Utc(2026, 11, 17, 12, 0));
        await AddSnapshot(Wednesday, ("YOG-1K", "Greek Yoghurt 1kg", 18m));
        await AddMovement(Wednesday, StockMovementKinds.Commit, "YOG-1K", -2m, 16m, Utc(2026, 11, 18, 6, 0));

        var result = (await new GetStockMovementsHandler(_context, _settings).Handle(
            new GetStockMovementsQuery(Shop, Utc(2026, 11, 17, 8, 0), Utc(2026, 11, 18, 9, 0), "YOG-1K"),
            CancellationToken.None)).Value;

        Assert.Equal([17m, 16m], result.Movements.Select(m => m.BalanceAfter));
        var opening = Assert.Single(result.Openings);
        Assert.Equal(Wednesday, opening.LedgerDay);
        Assert.Equal(18m, opening.Quantity);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Totals_net_sales_of_returns_and_keep_transfers_and_corrections_apart()
    {
        await AddMovement(Tuesday, StockMovementKinds.Commit, "FET-400", -5m, 15m, Utc(2026, 11, 17, 8, 0));
        await AddMovement(Tuesday, StockMovementKinds.Settle, "FET-400", -2m, 13m, Utc(2026, 11, 17, 9, 0));
        await AddMovement(Tuesday, StockMovementKinds.Release, "FET-400", 1m, 14m, Utc(2026, 11, 17, 10, 0));
        await AddMovement(Tuesday, StockMovementKinds.Transfer, "FET-400", 12m, 26m, Utc(2026, 11, 17, 11, 0));
        await AddMovement(Tuesday, StockMovementKinds.Transfer, "FET-400", -3m, 23m, Utc(2026, 11, 17, 12, 0));
        await AddMovement(Tuesday, StockMovementKinds.Reconciliation, "FET-400", -1m, 22m, Utc(2026, 11, 17, 13, 0));
        // Outside the window, and in another warehouse.
        await AddMovement(Tuesday, StockMovementKinds.Commit, "FET-400", -9m, 13m, Utc(2026, 11, 17, 15, 0));
        await AddMovement(Tuesday, StockMovementKinds.Commit, "FET-400", -4m, 40m, Utc(2026, 11, 17, 9, 0), "KEFGRS");

        var result = (await new GetStockMovementTotalsHandler(_context).Handle(
            new GetStockMovementTotalsQuery(Shop, Utc(2026, 11, 17, 7, 0), Utc(2026, 11, 17, 14, 0)),
            CancellationToken.None)).Value;

        var feta = Assert.Single(result.Items);
        Assert.Equal(6m, feta.Sold);
        Assert.Equal(12m, feta.TransferredIn);
        Assert.Equal(3m, feta.TransferredOut);
        Assert.Equal(-1m, feta.Corrected);
        Assert.Equal(6, feta.Movements);
    }

    [Fact]
    public async Task A_window_longer_than_a_month_is_refused()
    {
        var result = await new GetStockMovementsHandler(_context, _settings).Handle(
            new GetStockMovementsQuery(Shop, Utc(2026, 10, 1, 0, 0), Utc(2026, 11, 18, 0, 0)),
            CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<StockAsAtResult> AsAt(DateTime atUtc, FixedClock? clock = null)
    {
        var result = await new GetStockAsAtHandler(_context, _settings, clock ?? _clock)
            .Handle(new GetStockAsAtQuery(Shop, atUtc), CancellationToken.None);
        Assert.False(result.IsError);
        return result.Value;
    }

    private Task AddSnapshot(DateTime day, params (string Item, string Name, decimal Qty)[] rows) =>
        AddSnapshot(day, Shop, rows);

    private async Task AddSnapshot(DateTime day, string warehouse, params (string Item, string Name, decimal Qty)[] rows)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = day,
            WarehouseCode = warehouse,
            Status = StockSnapshotStatus.Complete,
            CreatedAt = day.AddHours(5),
            CompletedAt = day.AddHours(5).AddMinutes(2)
        };
        _context.DailyStockSnapshots.Add(snapshot);
        await _context.SaveChangesAsync();

        var batch = 0;
        foreach (var (item, name, qty) in rows)
        {
            _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
            {
                SnapshotId = snapshot.Id,
                ItemCode = item,
                ItemDescription = name,
                WarehouseCode = warehouse,
                BatchNumber = $"B{++batch}",
                // See SnapshotSqliteContext for why the concurrency token is set by hand here.
                Version = 1
            }.Opened(qty));
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task AddMovement(
        DateTime day, string kind, string item, decimal qty, decimal balance, DateTime occurredAtUtc, string warehouse = Shop)
    {
        _context.StockMovements.Add(new StockMovementEntity
        {
            LedgerDay = day,
            Kind = kind,
            ItemCode = item,
            WarehouseCode = warehouse,
            Quantity = qty,
            BalanceAfter = balance,
            Reference = "test",
            OccurredAt = occurredAtUtc
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static DateTime Utc(int y, int m, int d, int h, int min) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
