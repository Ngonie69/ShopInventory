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

        var read = await new GetLocalStockHandler(_context)
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

    // ── Helpers ─────────────────────────────────────────

    private static DateTime Today => DateTime.UtcNow.Date;

    private FetchDailyStockHandler Handler(ISAPServiceLayerClient sap) => new(
        _context,
        sap,
        StubProxy.Unused<IHubContext<NotificationHub>>(),
        Options.Create(new DailyStockSettings { MonitoredWarehouses = [Warehouse] }),
        NullLogger<FetchDailyStockHandler>.Instance);

    /// <summary>
    /// A warehouse holding one batch-managed line over two batches and one that is not batched at
    /// all — the shape the snapshot used to only see half of.
    /// </summary>
    private static ISAPServiceLayerClient Sap(
        List<StockQuantityDto>? unbatched = null,
        bool unbatchedThrows = false) =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) =>
                (object)Task.FromResult(new List<BatchNumber>
                {
                    new() { ItemCode = "CHE011", ItemName = "Feta 1kg", BatchNum = "B-1", Quantity = 4m, ExpiryDate = "2026-10-01" },
                    new() { ItemCode = "CHE011", ItemName = "Feta 1kg", BatchNum = "B-2", Quantity = 5m, ExpiryDate = "2026-11-01" }
                }),
            nameof(ISAPServiceLayerClient.GetNonBatchStockQuantitiesInWarehouseAsync) =>
                unbatchedThrows
                    ? throw new InvalidOperationException("SAP said no")
                    : Task.FromResult(unbatched ?? [Stock("BON001", "Bonaqua water 500ml", 24m)]),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

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
