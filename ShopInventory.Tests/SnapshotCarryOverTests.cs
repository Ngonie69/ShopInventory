using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;
using ShopInventory.Features.DesktopIntegration.Queries.GetLocalStock;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers a shop going on selling from yesterday's snapshot while today's is still being fetched.
/// </summary>
/// <remarks>
/// On 2026-09-17 the API restarted at 07:01, part-way through the morning fetch, and KEFSHOP's
/// snapshot for the day stayed Pending. From 07:00 every reader asked for today's snapshot only, so
/// the till's catalogue answered "still being loaded" and every sale was refused as untracked — for
/// over an hour, with yesterday's finished snapshot sitting unused. See
/// <see cref="StockSnapshotInForce"/> for the rule these tests hold it to.
///
/// <para>The days are resolved through <see cref="StockLedgerDay"/> exactly as the code under test
/// resolves them, so the tests assert the same thing at whatever hour the suite runs.</para>
/// </remarks>
public sealed class SnapshotCarryOverTests : IDisposable
{
    private const string Shop = "KEFSHOP";
    private const string Van = "VAN001";
    private const string Item = "CHE011";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<ApplicationDbContext> _otherContexts = [];

    private readonly DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = [Shop, Van],
        ReconcileWarehouses = [Shop]
    };

    public SnapshotCarryOverTests()
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
        foreach (var other in _otherContexts)
        {
            other.Dispose();
        }

        _context.Dispose();
        _connection.Dispose();
    }

    // ── The ledger ──────────────────────────────────────

    [Fact]
    public async Task A_shop_sells_from_yesterdays_snapshot_while_todays_is_being_fetched()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Shop, StockSnapshotStatus.Pending);

        var outcome = await Ledger().TryCommitAsync([Line(Shop, 3m)], "till sale", "desktop-sale:S-1");

        Assert.True(outcome.Committed);
        Assert.Empty(outcome.UntrackedWarehouses);
        Assert.Equal(7m, await AvailableAsync(Yesterday, Shop));
    }

    /// <summary>
    /// Journalled under today, not under the day of the rows it moved. That is what lets the fetch
    /// finishing today's snapshot find it, and what lets a retry after that be recognised.
    /// </summary>
    [Fact]
    public async Task A_sale_against_yesterdays_rows_is_journalled_under_today()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Shop, StockSnapshotStatus.Pending);

        await Ledger().TryCommitAsync([Line(Shop, 3m)], "till sale", "desktop-sale:S-1");

        var movement = Assert.Single(await _context.StockMovements.AsNoTracking().ToListAsync());
        Assert.Equal(Today, movement.LedgerDay);
        Assert.Equal(-3m, movement.Quantity);
        Assert.Equal(7m, movement.BalanceAfter);
    }

    [Fact]
    public async Task A_warehouse_with_no_snapshot_for_today_at_all_also_sells_from_yesterday()
    {
        // The minutes between 07:00 and the fetch creating today's header, and a day the fetch never
        // started. Neither is any different from a Pending snapshot to a cashier.
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);

        var outcome = await Ledger().TryCommitAsync([Line(Shop, 1m)], "till sale", null);

        Assert.True(outcome.Committed);
        Assert.Equal(9m, await AvailableAsync(Yesterday, Shop));
    }

    [Fact]
    public async Task A_failed_fetch_also_leaves_the_shop_on_yesterdays_snapshot()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Shop, StockSnapshotStatus.Failed);

        var outcome = await Ledger().TryCommitAsync([Line(Shop, 1m)], "till sale", null);

        Assert.True(outcome.Committed);
        Assert.Equal(9m, await AvailableAsync(Yesterday, Shop));
    }

    [Fact]
    public async Task Once_todays_snapshot_is_finished_the_ledger_moves_today_and_leaves_yesterday_alone()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Shop, StockSnapshotStatus.Complete, 5m);

        var outcome = await Ledger().TryCommitAsync([Line(Shop, 2m)], "till sale", null);

        Assert.True(outcome.Committed);
        Assert.Equal(3m, await AvailableAsync(Today, Shop));
        Assert.Equal(10m, await AvailableAsync(Yesterday, Shop));
    }

    /// <summary>
    /// A van's row is its morning load and the van reconciliation is computed from it, so a van day is
    /// never carried into the next one.
    /// </summary>
    [Fact]
    public async Task A_van_is_not_carried_over()
    {
        await SeedAsync(Yesterday, Van, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Van, StockSnapshotStatus.Pending);

        var outcome = await Ledger().TryCommitAsync([Line(Van, 1m)], "van sale", null);

        Assert.Equal([Van], outcome.UntrackedWarehouses);
        Assert.Equal(10m, await AvailableAsync(Yesterday, Van));
    }

    [Fact]
    public async Task A_snapshot_two_days_old_is_not_carried_over()
    {
        await SeedAsync(Today.AddDays(-2), Shop, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Shop, StockSnapshotStatus.Pending);

        var outcome = await Ledger().TryCommitAsync([Line(Shop, 1m)], "till sale", null);

        Assert.Equal([Shop], outcome.UntrackedWarehouses);
    }

    /// <summary>
    /// The case journalling under today exists for. Had the first commit been journalled under
    /// yesterday, the duplicate check — which reads today — would not see it, and the retry would take
    /// the units a second time out of the new snapshot.
    /// </summary>
    [Fact]
    public async Task A_sale_retried_after_todays_snapshot_lands_is_not_taken_twice()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);
        var today = await SeedAsync(Today, Shop, StockSnapshotStatus.Pending);

        await Ledger().TryCommitAsync([Line(Shop, 3m)], "till sale", "desktop-sale:S-1");
        await FinishAsync(today, 7m);

        var retry = await Ledger().TryCommitAsync([Line(Shop, 3m)], "till sale", "desktop-sale:S-1");

        Assert.True(retry.Committed);
        Assert.Equal(7m, await AvailableAsync(Today, Shop));
    }

    // ── The till's catalogue ────────────────────────────

    [Fact]
    public async Task The_till_is_offered_yesterdays_stock_while_todays_is_being_fetched()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Shop, StockSnapshotStatus.Pending);

        var result = await LocalStock().Handle(new GetLocalStockQuery(Shop), default);

        Assert.False(result.IsError);
        Assert.Equal(Yesterday, result.Value.SnapshotDate);
        Assert.Equal(10m, Assert.Single(result.Value.Items).AvailableQuantity);
    }

    [Fact]
    public async Task A_van_still_hears_that_its_snapshot_is_being_loaded()
    {
        await SeedAsync(Yesterday, Van, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Van, StockSnapshotStatus.Pending);

        var result = await LocalStock().Handle(new GetLocalStockQuery(Van), default);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SnapshotNotReady", result.FirstError.Code);
    }

    [Fact]
    public async Task A_day_the_till_names_is_the_day_it_gets_even_while_carrying_over()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);
        await SeedAsync(Today, Shop, StockSnapshotStatus.Pending);

        var result = await LocalStock().Handle(new GetLocalStockQuery(Shop, Today), default);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SnapshotNotReady", result.FirstError.Code);
    }

    // ── The fetch that finishes today's snapshot ────────

    /// <summary>
    /// The half that makes carrying over safe. SAP still holds all 9 — the sale has not posted — so a
    /// snapshot built from SAP alone would put the 3 units just sold straight back on the shelf.
    /// </summary>
    [Fact]
    public async Task A_sale_made_while_today_is_being_fetched_comes_off_the_finished_snapshot()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);

        var sap = Sap(duringRead: () =>
            OtherLedger().TryCommitAsync([Line(Shop, 3m)], "till sale", "desktop-sale:S-1"));

        var result = await Fetch(sap).FetchWarehouseStockAsync(Today, Shop, default);

        Assert.Equal("Complete", result.Status);
        Assert.Equal(7m, await AvailableAsync(Yesterday, Shop));
        Assert.Equal(6m, await AvailableAsync(Today, Shop));
    }

    [Fact]
    public async Task What_the_sale_takes_off_comes_from_the_soonest_expiring_batch()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);

        var sap = Sap(duringRead: () =>
            OtherLedger().TryCommitAsync([Line(Shop, 5m)], "till sale", "desktop-sale:S-1"));

        await Fetch(sap).FetchWarehouseStockAsync(Today, Shop, default);

        var rows = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.Snapshot.SnapshotDate == Today)
            .OrderBy(row => row.BatchNumber)
            .Select(row => row.AvailableQuantity)
            .ToListAsync();

        Assert.Equal([0m, 4m], rows);
    }

    /// <summary>
    /// A movement from before this fetch's reads is already accounted for — held back by the unposted
    /// sales read if SAP lacks it, absent from SAP's figure if not. Taking it off again would count it
    /// twice.
    /// </summary>
    [Fact]
    public async Task A_movement_from_before_the_fetch_began_is_not_taken_off_again()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 7m);

        StockMovementJournal.Append(
            _context, Today, StockMovementKinds.Commit, "desktop-sale:S-0", Item, Shop, -3m, 7m, "till sale");
        await _context.SaveChangesAsync();

        var earlier = await _context.StockMovements.SingleAsync();
        earlier.OccurredAt = DateTime.UtcNow.AddMinutes(-10);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await Fetch(Sap()).FetchWarehouseStockAsync(Today, Shop, default);

        Assert.Equal(9m, await AvailableAsync(Today, Shop));
    }

    [Fact]
    public async Task A_claim_released_during_the_fetch_takes_nothing_off()
    {
        await SeedAsync(Yesterday, Shop, StockSnapshotStatus.Complete, 10m);

        var sap = Sap(duringRead: async () =>
        {
            var ledger = OtherLedger();
            await ledger.TryCommitAsync([Line(Shop, 3m)], "web invoice", "invoice:INV-1");
            await ledger.ReleaseAsync([Line(Shop, 3m)], "invoice not posted", "invoice:INV-1");
        });

        await Fetch(sap).FetchWarehouseStockAsync(Today, Shop, default);

        Assert.Equal(9m, await AvailableAsync(Today, Shop));
    }

    /// <summary>
    /// On 2026-09-17 a fetch for KEFSHOP started by hand at 08:06 was still reading SAP when a second
    /// was started at 08:11. Each would have added a full set of rows to the same snapshot.
    /// </summary>
    [Fact]
    public async Task A_fetch_started_while_one_is_running_adds_nothing()
    {
        var gate = new StockFetchGate();
        WarehouseSnapshotResult? second = null;

        var sap = Sap(duringRead: async () =>
            second = await Fetch(Sap(), gate).FetchWarehouseStockAsync(Today, Shop, default));

        var first = await Fetch(sap, gate).FetchWarehouseStockAsync(Today, Shop, default);

        Assert.Equal("Complete", first.Status);
        Assert.Equal("AlreadyRunning", second?.Status);
        Assert.Equal(2, await _context.DailyStockSnapshotItems.CountAsync());
        Assert.Equal(9m, await AvailableAsync(Today, Shop));
    }

    [Fact]
    public async Task A_warehouse_can_be_fetched_again_once_the_last_fetch_has_finished()
    {
        var gate = new StockFetchGate();

        await Fetch(Sap(), gate).FetchWarehouseStockAsync(Today, Shop, default);
        _context.ChangeTracker.Clear();

        var again = await Fetch(Sap(), gate).FetchWarehouseStockAsync(Today, Shop, default);

        Assert.Equal("AlreadyExists", again.Status);
    }

    // ── Helpers ─────────────────────────────────────────

    private DateTime Today => StockLedgerDay.Today(_settings.StockFetchTimeCAT);

    private DateTime Yesterday => Today.AddDays(-1);

    private StockLedger Ledger()
    {
        _context.ChangeTracker.Clear();
        return new StockLedger(_context, Options.Create(_settings), NullLogger<StockLedger>.Instance);
    }

    /// <summary>
    /// A ledger on its own context over the same database, as a till request arriving mid-fetch has.
    /// </summary>
    private StockLedger OtherLedger()
    {
        var other = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _otherContexts.Add(other);

        return new StockLedger(other, Options.Create(_settings), NullLogger<StockLedger>.Instance);
    }

    private FetchDailyStockHandler Fetch(ISAPServiceLayerClient sap, StockFetchGate? gate = null) => new(
        _context,
        sap,
        StubProxy.Unused<IHubContext<NotificationHub>>(),
        Options.Create(_settings),
        StubProxy.Unused<ITransferEventListenerClient>(),
        Options.Create(new TransferEventListenerSettings()),
        gate ?? new StockFetchGate(),
        NullLogger<FetchDailyStockHandler>.Instance);

    /// <summary>
    /// SAP holding 9 of the item across two batches and nothing unbatched.
    /// </summary>
    /// <param name="duringRead">
    /// Run while the fetch is waiting on its batch read — the window in which, on a bad morning, a till
    /// goes on selling.
    /// </param>
    private static ISAPServiceLayerClient Sap(Func<Task>? duringRead = null) =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) => (object)BatchesAsync(duringRead),
            nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(new List<StockQuantityDto>
                {
                    new() { ItemCode = Item, ItemName = "Feta 1kg", WarehouseCode = Shop, InStock = 9m }
                }),
            nameof(ISAPServiceLayerClient.GetNonBatchStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(new List<StockQuantityDto>()),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    private static async Task<List<BatchNumber>> BatchesAsync(Func<Task>? duringRead)
    {
        if (duringRead is not null)
        {
            await duringRead();
        }

        return
        [
            new() { ItemCode = Item, ItemName = "Feta 1kg", BatchNum = "B-1", Quantity = 4m, ExpiryDate = "2026-10-01" },
            new() { ItemCode = Item, ItemName = "Feta 1kg", BatchNum = "B-2", Quantity = 5m, ExpiryDate = "2026-11-01" }
        ];
    }

    private GetLocalStockHandler LocalStock()
    {
        _context.ChangeTracker.Clear();
        return new GetLocalStockHandler(_context, Options.Create(_settings));
    }

    private static StockLedgerLine Line(string warehouse, decimal quantity) => new(Item, warehouse, quantity);

    private async Task<DailyStockSnapshotEntity> SeedAsync(
        DateTime day,
        string warehouse,
        StockSnapshotStatus status,
        decimal? quantity = null)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = day,
            WarehouseCode = warehouse,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };

        _context.DailyStockSnapshots.Add(snapshot);
        await _context.SaveChangesAsync();

        if (quantity is { } opening)
        {
            await AddRowAsync(snapshot, warehouse, opening);
        }

        return snapshot;
    }

    private async Task FinishAsync(DailyStockSnapshotEntity snapshot, decimal quantity)
    {
        _context.ChangeTracker.Clear();
        await AddRowAsync(snapshot, snapshot.WarehouseCode, quantity);

        var tracked = await _context.DailyStockSnapshots.SingleAsync(s => s.Id == snapshot.Id);
        tracked.Status = StockSnapshotStatus.Complete;
        await _context.SaveChangesAsync();
    }

    private async Task AddRowAsync(DailyStockSnapshotEntity snapshot, string warehouse, decimal quantity)
    {
        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = Item,
            ItemDescription = "Feta 1kg",
            WarehouseCode = warehouse,
            BatchNumber = "B-1",
            Version = 1
        }.Opened(quantity));

        await _context.SaveChangesAsync();
    }

    private async Task<decimal> AvailableAsync(DateTime day, string warehouse) =>
        await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.Snapshot.SnapshotDate == day && row.WarehouseCode == warehouse)
            .SumAsync(row => row.AvailableQuantity);
}
