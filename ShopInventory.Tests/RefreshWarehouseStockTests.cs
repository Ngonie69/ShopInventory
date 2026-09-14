using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.RefreshWarehouseStock;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Refreshing one warehouse's ledger from SAP by hand, for stock a goods receipt PO brought in that
/// the ledger was never told about.
/// </summary>
/// <remarks>
/// The first test is the reason the button exists. <c>StockLedgerReconcileTests</c> pins that the
/// hourly job leaves a row still at its morning figure alone, so a receipt against an item nobody has
/// sold today stays invisible until tomorrow. A refresh has to fix exactly that — and, like the job,
/// must not hand back till sales SAP has not seen yet, nor write any record of the change.
/// </remarks>
public sealed class RefreshWarehouseStockTests : IDisposable
{
    private const string Shop = "KEFSHOP";
    private const string Van = "VAN001";
    private const string Item = "VHU002";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private readonly List<StockQuantityDto> _warehouseStock = new();
    private readonly List<BatchNumber> _warehouseBatches = new();

    private readonly DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = [Shop, Van],
        ReconcileWarehouses = [Shop],
        ReconcileLedgerAgainstSap = true
    };

    public RefreshWarehouseStockTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
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
    public async Task A_receipt_against_an_item_untouched_since_the_morning_is_picked_up()
    {
        await SeedRowAsync(Shop, Item, original: 100m, available: 100m);
        _warehouseStock.Add(Stock(Item, inStock: 140m));

        var result = await RefreshAsync(Shop);

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.ItemsCorrected);
        Assert.Equal(140m, await AvailableAsync(Shop, Item));
    }

    [Fact]
    public async Task A_receipt_into_a_new_batch_lands_on_that_batch()
    {
        await SeedRowAsync(Shop, Item, original: 100m, available: 100m, batch: "B1");
        _warehouseStock.Add(Stock(Item, inStock: 140m));
        _warehouseBatches.Add(Batch(Item, "B1", 100m));
        _warehouseBatches.Add(Batch(Item, "B2", 40m, new DateTime(2027, 1, 31)));

        await RefreshAsync(Shop);

        var rows = await _context.DailyStockSnapshotItems.AsNoTracking()
            .Where(row => row.ItemCode == Item)
            .ToListAsync();

        Assert.Equal(100m, rows.Single(row => row.BatchNumber == "B1").AvailableQuantity);
        Assert.Equal(40m, rows.Single(row => row.BatchNumber == "B2").AvailableQuantity);
    }

    /// <summary>
    /// Ten sold at the till this morning, not posted yet; forty received in SAP since. SAP says 140
    /// because it has not heard about the ten. Copying it would sell those ten twice.
    /// </summary>
    [Fact]
    public async Task Unposted_till_sales_are_held_back_from_the_refresh()
    {
        await SeedRowAsync(Shop, Item, original: 100m, available: 90m);
        await SeedTillSaleAsync(Shop, Item, 10m, DesktopSaleConsolidationStatus.Pending);
        _warehouseStock.Add(Stock(Item, inStock: 140m));

        await RefreshAsync(Shop);

        Assert.Equal(130m, await AvailableAsync(Shop, Item));
    }

    [Fact]
    public async Task Stock_SAP_no_longer_holds_is_drawn_down()
    {
        await SeedRowAsync(Shop, Item, original: 100m, available: 100m);
        _warehouseStock.Add(Stock(Item, inStock: 60m));

        await RefreshAsync(Shop);

        Assert.Equal(60m, await AvailableAsync(Shop, Item));
    }

    [Fact]
    public async Task An_item_received_with_no_row_today_is_added()
    {
        await SeedRowAsync(Shop, Item, original: 5m, available: 5m);
        _warehouseStock.Add(Stock(Item, inStock: 5m));
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));

        var result = await RefreshAsync(Shop);

        Assert.Equal(1, result.Value.ItemsAdded);
        Assert.Equal(24m, await AvailableAsync(Shop, "NEW001"));
    }

    /// <summary>
    /// Asked for plainly: the refresh moves quantities and tracks nothing about the movement.
    /// </summary>
    [Fact]
    public async Task A_refresh_writes_no_movement_or_divergence_records()
    {
        await SeedRowAsync(Shop, Item, original: 100m, available: 100m);
        _warehouseStock.Add(Stock(Item, inStock: 140m));
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));

        await RefreshAsync(Shop);

        Assert.Empty(await _context.StockLedgerDivergences.AsNoTracking().ToListAsync());
        Assert.Empty(await _context.StockTransferAdjustments.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------
    // Refusals — each one leaves the ledger exactly as it was
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_van_is_refused_and_left_at_its_morning_load()
    {
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _warehouseStock.Add(Stock(Item, inStock: 40m));

        var result = await RefreshAsync(Van);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
        Assert.Equal(100m, await AvailableAsync(Van, Item));
    }

    [Fact]
    public async Task With_reconciliation_switched_off_the_refresh_is_refused()
    {
        _settings.ReconcileLedgerAgainstSap = false;
        await SeedRowAsync(Shop, Item, original: 100m, available: 100m);
        _warehouseStock.Add(Stock(Item, inStock: 140m));

        var result = await RefreshAsync(Shop);

        Assert.True(result.IsError);
        Assert.Equal(100m, await AvailableAsync(Shop, Item));
    }

    /// <summary>
    /// OITW holds a row for every item the warehouse has carried, so nothing back is a failed read.
    /// Treating it as an empty warehouse would zero the shop.
    /// </summary>
    [Fact]
    public async Task An_empty_SAP_read_is_refused_rather_than_zeroing_the_warehouse()
    {
        await SeedRowAsync(Shop, Item, original: 100m, available: 100m);

        var result = await RefreshAsync(Shop);

        Assert.True(result.IsError);
        Assert.Equal(100m, await AvailableAsync(Shop, Item));
    }

    [Fact]
    public async Task A_warehouse_with_no_finished_snapshot_is_refused()
    {
        _context.DailyStockSnapshots.Add(new DailyStockSnapshotEntity
        {
            SnapshotDate = LedgerDay,
            WarehouseCode = Shop,
            Status = StockSnapshotStatus.Pending
        });
        await _context.SaveChangesAsync();
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));

        var result = await RefreshAsync(Shop);

        Assert.True(result.IsError);
        Assert.Empty(await _context.DailyStockSnapshotItems.AsNoTracking().ToListAsync());
    }

    // ── Helpers ─────────────────────────────────────────

    private DateTime LedgerDay => StockLedgerDay.Today(_settings.StockFetchTimeCAT);

    private async Task<ErrorOr<RefreshWarehouseStockResult>> RefreshAsync(string warehouse)
    {
        var handler = new RefreshWarehouseStockHandler(
            _context,
            SapClient(),
            new StockLedger(_context, Options.Create(_settings), NullLogger<StockLedger>.Instance),
            Options.Create(_settings),
            NullLogger<RefreshWarehouseStockHandler>.Instance);

        var result = await handler.Handle(new RefreshWarehouseStockCommand(warehouse), CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    private ISAPServiceLayerClient SapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) =>
                Task.FromResult(_warehouseBatches.ToList()),

            nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(_warehouseStock.ToList()),

            // Anything else would be a second SAP round trip the whole-warehouse reads already answered.
            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private static StockQuantityDto Stock(string itemCode, decimal inStock) => new()
    {
        ItemCode = itemCode,
        ItemName = itemCode,
        WarehouseCode = Shop,
        InStock = inStock,
        Committed = 0m
    };

    private static BatchNumber Batch(string itemCode, string batchNum, decimal quantity, DateTime? expiry = null) => new()
    {
        ItemCode = itemCode,
        BatchNum = batchNum,
        Quantity = quantity,
        Warehouse = Shop,
        ExpiryDate = expiry?.ToString("yyyy-MM-dd")
    };

    private async Task<decimal> AvailableAsync(string warehouse, string itemCode)
        => await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.WarehouseCode == warehouse && row.ItemCode == itemCode)
            .SumAsync(row => row.AvailableQuantity);

    private async Task SeedRowAsync(
        string warehouse,
        string itemCode,
        decimal original,
        decimal available,
        string? batch = null)
    {
        var day = LedgerDay;

        var snapshot = await _context.DailyStockSnapshots
            .FirstOrDefaultAsync(s => s.SnapshotDate == day && s.WarehouseCode == warehouse);

        if (snapshot is null)
        {
            snapshot = new DailyStockSnapshotEntity
            {
                SnapshotDate = day,
                WarehouseCode = warehouse,
                Status = StockSnapshotStatus.Complete
            };
            _context.DailyStockSnapshots.Add(snapshot);
            await _context.SaveChangesAsync();
        }

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = itemCode,
            WarehouseCode = warehouse,
            BatchNumber = batch,
            OriginalQuantity = original,
            AvailableQuantity = available,
            // See SnapshotSqliteContext for why the concurrency token is set by hand here.
            Version = 1
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedTillSaleAsync(
        string warehouse,
        string itemCode,
        decimal quantity,
        DesktopSaleConsolidationStatus status)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"TILL-{Guid.NewGuid():N}",
            CardCode = "CASH",
            ConsolidationStatus = status,
            CreatedAt = DateTime.UtcNow,
            DocDate = DateTime.UtcNow.Date
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();

        _context.DesktopSaleLines.Add(new DesktopSaleLineEntity
        {
            SaleId = sale.Id,
            LineNum = 0,
            ItemCode = itemCode,
            WarehouseCode = warehouse,
            Quantity = quantity
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
