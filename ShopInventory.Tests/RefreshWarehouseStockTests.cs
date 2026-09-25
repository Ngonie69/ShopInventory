using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using Microsoft.AspNetCore.SignalR;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;
using ShopInventory.Hubs;
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
    private bool _batchReadFails;

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

    /// <summary>
    /// A snapshot still being fetched is left to that fetch: a second one would write a second set of
    /// rows into it.
    /// </summary>
    [Fact]
    public async Task A_snapshot_still_being_fetched_is_refused()
    {
        await SeedHeaderAsync(StockSnapshotStatus.Pending);
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));

        var result = await RefreshAsync(Shop);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
        Assert.Empty(await _context.DailyStockSnapshotItems.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------
    // No usable snapshot — fetched again rather than refused
    // ---------------------------------------------------------------

    /// <summary>
    /// 2026-09-25: the morning fetch failed for KEFSHOP and the button answered "fetch today's stock
    /// first". A failed snapshot has no rows to correct, so the refresh fetches it again.
    /// </summary>
    [Fact]
    public async Task A_failed_snapshot_is_fetched_again()
    {
        await SeedHeaderAsync(StockSnapshotStatus.Failed, lastError: "SAP timed out");
        _warehouseStock.Add(Stock(Item, inStock: 140m));
        _warehouseBatches.Add(Batch(Item, "B1", 140m));

        var result = await RefreshAsync(Shop);

        Assert.False(result.IsError);
        Assert.True(result.Value.SnapshotRefetched);
        Assert.Equal(1, result.Value.RowsFetched);
        Assert.Equal(140m, await AvailableAsync(Shop, Item));

        var header = await _context.DailyStockSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(StockSnapshotStatus.Complete, header.Status);
        Assert.Null(header.LastError);
    }

    [Fact]
    public async Task A_day_never_fetched_is_fetched()
    {
        _warehouseStock.Add(Stock(Item, inStock: 30m));
        _warehouseBatches.Add(Batch(Item, "B1", 30m));

        var result = await RefreshAsync(Shop);

        Assert.False(result.IsError);
        Assert.True(result.Value.SnapshotRefetched);
        Assert.Equal(30m, await AvailableAsync(Shop, Item));
    }

    /// <summary>
    /// The fetch nets unposted till sales as the correction does, so re-fetching cannot hand back
    /// units a till already sold.
    /// </summary>
    [Fact]
    public async Task A_refetch_holds_back_unposted_till_sales()
    {
        await SeedHeaderAsync(StockSnapshotStatus.Failed);
        await SeedTillSaleAsync(Shop, Item, 10m, DesktopSaleConsolidationStatus.Pending);
        _warehouseStock.Add(Stock(Item, inStock: 140m));
        _warehouseBatches.Add(Batch(Item, "B1", 140m));

        await RefreshAsync(Shop);

        Assert.Equal(130m, await AvailableAsync(Shop, Item));
    }

    /// <summary>
    /// The operator is told why, and the reason stays on the snapshot for the page to show.
    /// </summary>
    [Fact]
    public async Task A_refetch_that_fails_again_says_why()
    {
        await SeedHeaderAsync(StockSnapshotStatus.Failed);
        _batchReadFails = true;

        var result = await RefreshAsync(Shop);

        Assert.True(result.IsError);
        Assert.Contains("Service Layer did not answer", result.FirstError.Description);

        var header = await _context.DailyStockSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(StockSnapshotStatus.Failed, header.Status);
        Assert.Contains("Service Layer did not answer", header.LastError);
    }

    // ── Helpers ─────────────────────────────────────────

    private DateTime LedgerDay => StockLedgerDay.Today(_settings.StockFetchTimeCAT);

    private async Task<ErrorOr<RefreshWarehouseStockResult>> RefreshAsync(string warehouse)
    {
        var sap = SapClient();

        var handler = new RefreshWarehouseStockHandler(
            _context,
            sap,
            new StockLedger(_context, Options.Create(_settings), NullLogger<StockLedger>.Instance),
            Options.Create(_settings),
            new FetchDailyStockHandler(
                _context,
                sap,
                StubProxy.Unused<IHubContext<NotificationHub>>(),
                Options.Create(_settings),
                StubProxy.Unused<ITransferEventListenerClient>(),
                Options.Create(new TransferEventListenerSettings()),
                new StockFetchGate(),
                NullLogger<FetchDailyStockHandler>.Instance),
            NullLogger<RefreshWarehouseStockHandler>.Instance);

        var result = await handler.Handle(new RefreshWarehouseStockCommand(warehouse), CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    private ISAPServiceLayerClient SapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) => _batchReadFails
                ? Task.FromException<List<BatchNumber>>(new TimeoutException("Service Layer did not answer"))
                : Task.FromResult(_warehouseBatches.ToList()),

            nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(_warehouseStock.ToList()),

            // Only the re-fetch of a failed snapshot asks for this; SAP holding nothing unbatched.
            nameof(ISAPServiceLayerClient.GetNonBatchStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(new List<StockQuantityDto>()),

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

    private async Task SeedHeaderAsync(StockSnapshotStatus status, string? lastError = null)
    {
        _context.DailyStockSnapshots.Add(new DailyStockSnapshotEntity
        {
            SnapshotDate = LedgerDay,
            WarehouseCode = Shop,
            Status = status,
            LastError = lastError
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

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
            // See SnapshotSqliteContext for why the concurrency token is set by hand here.
            Version = 1
        }.MovedTo(original, available));

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
