using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;
using ShopInventory.Features.DesktopIntegration.Queries.GetLocalStock;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the daily snapshot holding the whole warehouse, not only the part of it SAP manages by
/// batch.
/// </summary>
/// <remarks>
/// The morning read was <c>OBTN ⋈ OBTQ</c> and nothing else, so an item SAP does not batch-manage
/// had no row to join to and never entered the snapshot — and since the till sells from the
/// snapshot, a shop could hold a case of a bought-in line, see it on the shelf, and not find it on
/// the screen. The Complimentary Products group is the whole of it: bought-in resale stock, none of
/// it batched. The till carried a hard-coded row for CON020 for the same reason, one item at a time.
///
/// Every assertion here reads the rows back out of storage, and the till-facing query is run over
/// them, because "the handler built a list" is not the thing that was broken.
/// </remarks>
public sealed class UnbatchedStockSnapshotTests : IDisposable
{
    private const string Warehouse = "KEFSHOP";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public UnbatchedStockSnapshotTests()
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
    public async Task An_item_with_no_batches_reaches_the_snapshot()
    {
        await Handler(Sap()).FetchWarehouseStockAsync(Today, Warehouse, default);

        var row = await RowAsync("BON001");

        Assert.Equal("Bonaqua water 500ml", row.ItemDescription);
        Assert.Null(row.BatchNumber);
        Assert.Equal(24m, row.AvailableQuantity);
        Assert.Equal(24m, row.OriginalQuantity);
    }

    [Fact]
    public async Task The_till_is_offered_the_unbatched_item()
    {
        await Handler(Sap()).FetchWarehouseStockAsync(Today, Warehouse, default);
        _context.ChangeTracker.Clear();

        var read = await new GetLocalStockHandler(
                _context,
                Options.Create(new DailyStockSettings { MonitoredWarehouses = [Warehouse] }))
            .Handle(new GetLocalStockQuery(Warehouse, Today), default);

        Assert.False(read.IsError);
        var item = Assert.Single(read.Value.Items, entry => entry.ItemCode == "BON001");
        Assert.Equal(24m, item.AvailableQuantity);
        Assert.DoesNotContain(item.Batches, batch => batch.BatchNumber is not null);
    }

    [Fact]
    public async Task Batch_managed_stock_is_still_read_from_its_batches()
    {
        await Handler(Sap()).FetchWarehouseStockAsync(Today, Warehouse, default);

        var rows = await RowsAsync("CHE011");

        Assert.Equal(["B-1", "B-2"], rows.Select(row => row.BatchNumber));
        Assert.Equal(9m, rows.Sum(row => row.AvailableQuantity));
    }

    /// <summary>
    /// The SQL excludes batch-managed items, so this is the case where SAP's own two answers
    /// disagree — a flag flipped between the reads, say. The batch rows win, because they are the
    /// ones a sale can be allocated against; counting both would offer a cashier stock twice over.
    /// </summary>
    [Fact]
    public async Task An_item_answered_by_both_reads_is_counted_once()
    {
        var sap = Sap(unbatched: [Stock("CHE011", "Feta 1kg", 40m), Stock("BON001", "Bonaqua water 500ml", 24m)]);

        await Handler(sap).FetchWarehouseStockAsync(Today, Warehouse, default);

        var rows = await RowsAsync("CHE011");
        Assert.Equal(2, rows.Count);
        Assert.Equal(9m, rows.Sum(row => row.AvailableQuantity));
        Assert.DoesNotContain(rows, row => row.BatchNumber is null);
    }

    [Fact]
    public async Task A_warehouse_holding_none_of_an_item_does_not_list_it()
    {
        var sap = Sap(unbatched: [Stock("BON001", "Bonaqua water 500ml", 0m)]);

        await Handler(sap).FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Empty(await RowsAsync("BON001"));
    }

    /// <summary>
    /// The batch half is still worth putting on a till, so the snapshot completes — but a shop short
    /// of exactly its unbatched lines must not look like a shop that holds none, which is the failure
    /// this read exists to fix. The reason is recorded on the snapshot.
    /// </summary>
    [Fact]
    public async Task A_failed_unbatched_read_completes_the_snapshot_and_records_why()
    {
        var sap = Sap(unbatchedThrows: true);

        var result = await Handler(sap).FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal("Complete", result.Status);
        Assert.Equal(2, result.ItemCount);

        var snapshot = await SnapshotAsync();
        Assert.Equal(StockSnapshotStatus.Complete, snapshot.Status);
        Assert.Contains("SAP said no", snapshot.LastError);
    }

    [Fact]
    public async Task A_successful_read_leaves_no_error_behind()
    {
        await Handler(Sap()).FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Null((await SnapshotAsync()).LastError);
    }

    /// <summary>
    /// The point of the fallback: this API's own SAP read queues behind six process-wide slots, and
    /// TransferEventListener holds a Service Layer session of its own, so it can still answer when
    /// ours cannot. Without it the shop loses exactly its unbatched lines for the day.
    /// </summary>
    [Fact]
    public async Task The_listener_supplies_unbatched_stock_when_our_own_read_fails()
    {
        var listener = FakeListener.Returning(ListenerStock("BON001", "Bonaqua water 500ml", 24m));

        var result = await Handler(Sap(unbatchedThrows: true), listener)
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal("Complete", result.Status);

        var row = await RowAsync("BON001");
        Assert.Equal(24m, row.AvailableQuantity);
        Assert.Null(row.BatchNumber);
    }

    /// <summary>
    /// A row that arrived by the back door still has to be recorded as such: the primary read failing
    /// is worth knowing about even on a day the fallback covered for it completely.
    /// </summary>
    [Fact]
    public async Task A_fallback_reading_is_recorded_on_the_snapshot()
    {
        var listener = FakeListener.Returning(ListenerStock("BON001", "Bonaqua water 500ml", 24m));

        await Handler(Sap(unbatchedThrows: true), listener)
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        var snapshot = await SnapshotAsync();
        Assert.Contains("TransferEventListener", snapshot.LastError);
        Assert.Contains("SAP said no", snapshot.LastError);
    }

    /// <summary>
    /// The listener computes Available as InStock - Committed + Ordered, so it counts stock still on
    /// order. A till needs what is on the shelf; taking the wrong figure would offer a cashier twenty
    /// cases sitting on a supplier's truck.
    /// </summary>
    [Fact]
    public async Task Stock_on_order_is_not_offered_to_the_till()
    {
        var listener = FakeListener.Returning(new TransferListenerItemQuantityDto
        {
            ItemCode = "BON001",
            ItemName = "Bonaqua water 500ml",
            WarehouseCode = Warehouse,
            InStock = 4m,
            Committed = 0m,
            Ordered = 20m,
            Available = 24m
        });

        await Handler(Sap(unbatchedThrows: true), listener)
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal(4m, (await RowAsync("BON001")).AvailableQuantity);
    }

    /// <summary>
    /// The listener answers 503 rather than an empty list when it cannot reach SAP, precisely so a
    /// failed read is never mistaken for an empty warehouse. That distinction has to survive here too.
    /// </summary>
    [Fact]
    public async Task A_listener_that_cannot_reach_sap_adds_no_rows()
    {
        var result = await Handler(Sap(unbatchedThrows: true), FakeListener.Unavailable())
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal("Complete", result.Status);
        Assert.Empty(await RowsAsync("BON001"));
        Assert.Contains("could not reach SAP", (await SnapshotAsync()).LastError);
    }

    [Fact]
    public async Task A_listener_that_throws_leaves_the_batch_half_on_the_till()
    {
        var result = await Handler(Sap(unbatchedThrows: true), FakeListener.Throwing("connection refused"))
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal("Complete", result.Status);
        Assert.Equal(2, result.ItemCount);
        Assert.Contains("connection refused", (await SnapshotAsync()).LastError);
    }

    /// <summary>
    /// The degraded scan covers only the listener's configured item groups, so it can be short without
    /// saying which lines are missing. It is accepted by default because some of the shelf beats none
    /// of it, and the shortfall is spelled out on the snapshot because it is invisible in the data.
    /// </summary>
    [Fact]
    public async Task A_degraded_listener_reading_is_accepted_and_says_so()
    {
        var listener = FakeListener.Returning(
            ListenerStock("BON001", "Bonaqua water 500ml", 24m),
            source: "item-scan-fallback");

        await Handler(Sap(unbatchedThrows: true), listener)
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal(24m, (await RowAsync("BON001")).AvailableQuantity);
        Assert.Contains("degraded item scan", (await SnapshotAsync()).LastError);
    }

    [Fact]
    public async Task A_degraded_reading_can_be_refused_by_configuration()
    {
        var listener = FakeListener.Returning(
            ListenerStock("BON001", "Bonaqua water 500ml", 24m),
            source: "item-scan-fallback");

        await Handler(
                Sap(unbatchedThrows: true),
                listener,
                new TransferEventListenerSettings { AcceptDegradedStockFallback = false })
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Empty(await RowsAsync("BON001"));
    }

    [Fact]
    public async Task The_fallback_is_not_consulted_when_it_is_switched_off()
    {
        var listener = FakeListener.Throwing("must not be called");

        await Handler(
                Sap(unbatchedThrows: true),
                listener,
                new TransferEventListenerSettings { UseForUnbatchedStockFallback = false })
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.False(listener.WasCalled);
        Assert.Contains("SAP said no", (await SnapshotAsync()).LastError);
    }

    /// <summary>
    /// The listener costs nothing on a normal day: it is only ever reached after the primary read has
    /// thrown, and a stub that fails when touched is how that stays true.
    /// </summary>
    [Fact]
    public async Task The_listener_is_not_called_when_our_own_read_works()
    {
        var listener = FakeListener.Throwing("must not be called");

        await Handler(Sap(), listener).FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.False(listener.WasCalled);
        Assert.Null((await SnapshotAsync()).LastError);
    }

    // ── Which read owns the unbatched half ──────────────

    [Fact]
    public async Task The_dedicated_read_owns_the_unbatched_half_not_the_composer()
    {
        // SnapshotComposer can also produce a row for an item with no batches, from the warehouse
        // read it already has. Keeping those made this dedicated read redundant for every item the
        // warehouse read listed -- which is all of them, it being the same table unfiltered -- and
        // with nothing left to do, its TransferEventListener fallback could never run.
        //
        // So: the warehouse read here lists BON001, and the composer would happily emit a row for
        // it. The row that lands must be the dedicated read's, at its quantity, not the composer's.
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) =>
                (object)Task.FromResult(new List<BatchNumber>()),
            nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(new List<StockQuantityDto> { Stock("BON001", "Bonaqua water 500ml", 99m) }),
            nameof(ISAPServiceLayerClient.GetNonBatchStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(new List<StockQuantityDto> { Stock("BON001", "Bonaqua water 500ml", 24m) }),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        await Handler(sap).FetchWarehouseStockAsync(Today, Warehouse, default);

        var row = Assert.Single(await _context.DailyStockSnapshotItems.AsNoTracking().ToListAsync());
        Assert.Equal("BON001", row.ItemCode);
        Assert.Equal(24m, row.AvailableQuantity);
    }

    [Fact]
    public async Task A_failed_warehouse_read_still_reaches_the_unbatched_read()
    {
        // The commitment read sits in front of the unbatched one. If a failure there were fatal, the
        // snapshot would die before the fallback written for exactly the SAP-slot starvation most
        // likely to cause it -- and the till would lose the whole warehouse rather than one figure.
        await Handler(Sap(warehouseReadThrows: true)).FetchWarehouseStockAsync(Today, Warehouse, default);

        var rows = await _context.DailyStockSnapshotItems.AsNoTracking().ToListAsync();

        // The unbatched half arrived.
        Assert.Contains(rows, row => row.ItemCode == "BON001" && row.AvailableQuantity == 24m);

        // And the batch half is recorded gross -- worse than composed, which is the trade, but there.
        Assert.Equal(9m, rows.Where(row => row.ItemCode == "CHE011").Sum(row => row.AvailableQuantity));
        Assert.Equal(StockSnapshotStatus.Complete, (await SnapshotAsync()).Status);
    }

    // ── Repairing a snapshot that lost its unbatched half ───────────────

    [Fact]
    public async Task A_snapshot_that_lost_its_unbatched_half_says_so()
    {
        await Handler(Sap(unbatchedThrows: true)).FetchWarehouseStockAsync(Today, Warehouse, default);

        var snapshot = await SnapshotAsync();

        Assert.Equal(StockSnapshotStatus.Complete, snapshot.Status);
        Assert.True(snapshot.UnbatchedStockMissing);
    }

    [Fact]
    public async Task A_whole_snapshot_does_not_claim_to_be_missing_anything()
    {
        await Handler(Sap()).FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.False((await SnapshotAsync()).UnbatchedStockMissing);
    }

    [Fact]
    public async Task An_unbatched_half_the_listener_supplied_is_not_treated_as_missing()
    {
        // The note is written on the fallback's success too, so LastError alone cannot answer this.
        var listener = FakeListener.Returning(ListenerStock("BON001", "Bonaqua water 500ml", 24m));

        await Handler(Sap(unbatchedThrows: true), listener)
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        var snapshot = await SnapshotAsync();

        Assert.NotNull(snapshot.LastError);
        Assert.False(snapshot.UnbatchedStockMissing);
    }

    [Fact]
    public async Task A_second_fetch_fills_in_the_half_the_first_could_not_read()
    {
        await Handler(Sap(unbatchedThrows: true)).FetchWarehouseStockAsync(Today, Warehouse, default);
        Assert.Empty(await RowsAsync("BON001"));
        _context.ChangeTracker.Clear();

        var result = await Handler(Sap()).FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal("ToppedUp", result.Status);
        Assert.Equal(24m, (await RowAsync("BON001")).AvailableQuantity);

        var snapshot = await SnapshotAsync();
        Assert.False(snapshot.UnbatchedStockMissing);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public async Task A_top_up_leaves_what_the_day_has_already_sold_alone()
    {
        await Handler(Sap(unbatchedThrows: true)).FetchWarehouseStockAsync(Today, Warehouse, default);

        // The morning's 9 units of CHE011, sold down to 2 by the time anyone notices the gap. SAP
        // still holds 9 — desktop sales only reach it at end-of-day consolidation — so re-reading
        // the batch half here would hand 7 units back to the till.
        var sold = await _context.DailyStockSnapshotItems
            .Where(row => row.ItemCode == "CHE011")
            .ToListAsync();
        sold[0].AvailableQuantity = 2m;
        sold[1].AvailableQuantity = 0m;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await Handler(Sap()).FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal(2m, (await RowsAsync("CHE011")).Sum(row => row.AvailableQuantity));
        Assert.Equal(24m, (await RowAsync("BON001")).AvailableQuantity);
    }

    [Fact]
    public async Task A_top_up_that_fails_again_leaves_the_snapshot_repairable()
    {
        await Handler(Sap(unbatchedThrows: true)).FetchWarehouseStockAsync(Today, Warehouse, default);
        _context.ChangeTracker.Clear();

        var result = await Handler(Sap(unbatchedThrows: true))
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal("UnbatchedStillMissing", result.Status);
        Assert.True((await SnapshotAsync()).UnbatchedStockMissing);
    }

    [Fact]
    public async Task A_snapshot_with_nothing_missing_is_still_skipped()
    {
        await Handler(Sap()).FetchWarehouseStockAsync(Today, Warehouse, default);
        _context.ChangeTracker.Clear();

        // A SAP that refuses every call: reaching it at all would mean the skip was not taken.
        var result = await Handler(StubProxy.For<ISAPServiceLayerClient>((method, _) =>
                throw new InvalidOperationException($"SAP must not be called: {method.Name}")))
            .FetchWarehouseStockAsync(Today, Warehouse, default);

        Assert.Equal("AlreadyExists", result.Status);
        Assert.Single(await RowsAsync("BON001"));
    }

    // ── Helpers ─────────────────────────────────────────

    private static DateTime Today => DateTime.UtcNow.Date;

    private FetchDailyStockHandler Handler(
        ISAPServiceLayerClient sap,
        ITransferEventListenerClient? listener = null,
        TransferEventListenerSettings? listenerSettings = null) => new(
        _context,
        sap,
        StubProxy.Unused<IHubContext<NotificationHub>>(),
        Options.Create(new DailyStockSettings { MonitoredWarehouses = [Warehouse] }),
        listener ?? FakeListener.Disabled(),
        Options.Create(listenerSettings ?? new TransferEventListenerSettings()),
        NullLogger<FetchDailyStockHandler>.Instance);

    /// <summary>
    /// A warehouse holding one batch-managed line over two batches and one that is not batched at
    /// all — the shape the snapshot used to only see half of.
    /// </summary>
    private static ISAPServiceLayerClient Sap(
        List<StockQuantityDto>? unbatched = null,
        bool unbatchedThrows = false,
        bool warehouseReadThrows = false) =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) =>
                (object)Task.FromResult(new List<BatchNumber>
                {
                    new() { ItemCode = "CHE011", ItemName = "Feta 1kg", BatchNum = "B-1", Quantity = 4m, ExpiryDate = "2026-10-01" },
                    new() { ItemCode = "CHE011", ItemName = "Feta 1kg", BatchNum = "B-2", Quantity = 5m, ExpiryDate = "2026-11-01" }
                }),

            // The commitment read. It answers the batch-managed line only, and nothing is committed,
            // so composition leaves the batch quantities as they are and these tests keep asserting
            // what they always did. It has to answer at all, though: the handler treats a failure
            // here as "record the batch half gross", so a stub that refused the call would put every
            // test in this class on the degraded path without any of them saying so.
            nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                warehouseReadThrows
                    ? throw new InvalidOperationException("SAP said no to the warehouse read")
                    : Task.FromResult(new List<StockQuantityDto> { Stock("CHE011", "Feta 1kg", 9m) }),
            nameof(ISAPServiceLayerClient.GetNonBatchStockQuantitiesInWarehouseAsync) =>
                unbatchedThrows
                    ? throw new InvalidOperationException("SAP said no")
                    : Task.FromResult(unbatched ?? [Stock("BON001", "Bonaqua water 500ml", 24m)]),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    private static TransferListenerItemQuantityDto ListenerStock(
        string itemCode, string itemName, decimal inStock) => new()
    {
        ItemCode = itemCode,
        ItemName = itemName,
        WarehouseCode = Warehouse,
        InStock = inStock,
        Available = inStock
    };

    /// <summary>
    /// Hand-written rather than a <see cref="StubProxy"/>: the interface is small, and two of its
    /// members are properties, which read badly through a dispatch proxy.
    /// </summary>
    private sealed class FakeListener : ITransferEventListenerClient
    {
        private TransferListenerWarehouseStockDto? _reading;
        private Exception? _failure;
        private bool _enabled = true;

        public bool WasCalled { get; private set; }

        public bool IsEnabled => _enabled;

        public string BaseUrl => "http://listener.test";

        public static FakeListener Returning(
            TransferListenerItemQuantityDto item,
            string source = "sql-query") => new()
            {
                _reading = new TransferListenerWarehouseStockDto
                {
                    WarehouseCode = Warehouse,
                    ItemCount = 1,
                    Source = source,
                    Items = [item]
                }
            };

        /// <summary>Answered, and could not establish anything. Not an empty warehouse.</summary>
        public static FakeListener Unavailable() => new();

        public static FakeListener Throwing(string message) =>
            new() { _failure = new HttpRequestException(message) };

        public static FakeListener Disabled() => new() { _enabled = false };

        public Task<TransferListenerWarehouseStockDto?> GetWarehouseNonBatchStockAsync(
            string warehouseCode, CancellationToken cancellationToken = default)
        {
            WasCalled = true;

            return _failure is not null
                ? Task.FromException<TransferListenerWarehouseStockDto?>(_failure)
                : Task.FromResult(_reading);
        }

        public Task<TransferListenerHealthDto> GetHealthAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the snapshot path.");

        public Task<TransferListenerStatsDto> GetStatsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the snapshot path.");

        public Task<IReadOnlyList<string>> GetMonitoredWarehousesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the snapshot path.");

        public Task<TransferListenerCheckResultDto> TriggerCheckAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the snapshot path.");
    }

    private static StockQuantityDto Stock(string itemCode, string itemName, decimal inStock) => new()
    {
        ItemCode = itemCode,
        ItemName = itemName,
        WarehouseCode = Warehouse,
        InStock = inStock
    };

    private async Task<DailyStockSnapshotEntity> SnapshotAsync() =>
        await _context.DailyStockSnapshots
            .AsNoTracking()
            .SingleAsync(snapshot => snapshot.WarehouseCode == Warehouse && snapshot.SnapshotDate == Today);

    private async Task<DailyStockSnapshotItemEntity> RowAsync(string itemCode) =>
        Assert.Single(await RowsAsync(itemCode));

    private async Task<List<DailyStockSnapshotItemEntity>> RowsAsync(string itemCode) =>
        await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.WarehouseCode == Warehouse && row.ItemCode == itemCode)
            .OrderBy(row => row.BatchNumber)
            .ToListAsync();

    /// <summary>
    /// <see cref="DailyStockSnapshotItemEntity.Version"/> is <c>[Timestamp]</c>, which Npgsql maps
    /// to the store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the
    /// column out of the INSERT and the NOT NULL constraint fails. Making it an ordinary property
    /// lets the fixture supply one; nothing under test reads it.
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
