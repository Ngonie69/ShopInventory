using ErrorOr;
using MediatR;
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
using ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;
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

    /// <summary>The stock transfers SAP has booked for the warehouse refreshed.</summary>
    private readonly List<SapStockMovementDocument> _sapTransfers = new();

    /// <summary>What reading SAP's transfers throws, when it should fail.</summary>
    private Exception? _transferReadFailure;

    private int _transfersDelivered;

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

    /// <summary>
    /// The button pressed between SAP booking a transfer out of the shop and the listener delivering
    /// it. The refresh moves the shop to SAP's figure, which has the transfer already, so the delivery
    /// that follows must find it recorded rather than take it off a second time.
    /// </summary>
    [Fact]
    public async Task A_transfer_SAP_booked_before_the_press_is_not_taken_again_by_the_webhook()
    {
        await SeedRowAsync(Shop, Item, original: 100m, available: 100m);
        _warehouseStock.Add(Stock(Item, inStock: 80m));
        _sapTransfers.Add(new SapStockMovementDocument
        {
            DocEntry = 6001,
            DocNum = 9001,
            FromWarehouse = Shop,
            ToWarehouse = "VAN006",
            StockTransferLines =
            [
                new SapStockMovementLine { ItemCode = Item, Quantity = 20, FromWarehouseCode = Shop, WarehouseCode = "VAN006" }
            ]
        });

        var result = await RefreshAsync(Shop);

        Assert.False(result.IsError);
        Assert.Equal(80m, await AvailableAsync(Shop, Item));

        await using var webhookContext = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

        var delivery = await new ProcessTransferEventHandler(
                webhookContext,
                Options.Create(_settings),
                new CapturingPublisher(),
                NullLogger<ProcessTransferEventHandler>.Instance)
            .Handle(new ProcessTransferEventCommand(Item, Shop, "VAN006", 20m, 6001, 9001), CancellationToken.None);

        Assert.False(delivery.Value.Adjusted);
        Assert.Equal(80m, await AvailableAsync(Shop, Item));
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
    // Vans — brought up to date by SAP's transfers, never by SAP's figure
    // ---------------------------------------------------------------

    /// <summary>
    /// The reason vans can be refreshed at all: a load transfer made after the 07:00 fetch. The
    /// refresh reads it from SAP and delivers it through the transfer webhook's handler.
    /// </summary>
    [Fact]
    public async Task A_van_picks_up_a_transfer_made_after_its_morning_load()
    {
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _sapTransfers.Add(Transfer(88901, "KEFGRC", Van, 24));

        var result = await RefreshAsync(Van);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.True(result.Value.ByTransfer);
        Assert.Equal(1, result.Value.TransfersApplied);
        Assert.Equal(1, result.Value.TransfersToday);
        Assert.Equal(0, result.Value.TransfersNotApplied);
        Assert.Equal(124m, await AvailableAsync(Van, Item));
    }

    /// <summary>
    /// SAP's book figure for a van already has the day's online invoices off it, while the van's row
    /// loses its sales only at end of day. Copying SAP would take those sales twice — so a van refresh
    /// reads SAP's transfers and nothing else, and the morning load must stand.
    /// </summary>
    [Fact]
    public async Task A_van_is_never_set_to_SAPs_figure()
    {
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _warehouseStock.Add(Stock(Item, inStock: 40m));

        var result = await RefreshAsync(Van, stockReadsForbidden: true);

        Assert.False(result.IsError);
        Assert.Equal(0, result.Value.TransfersApplied);
        Assert.Equal(100m, await AvailableAsync(Van, Item));

        var row = await _context.DailyStockSnapshotItems.AsNoTracking().SingleAsync(item => item.WarehouseCode == Van);
        Assert.Equal(100m, row.OriginalQuantity);
    }

    /// <summary>
    /// A line the ledger already applied is not applied again when a later press reads it once more.
    /// </summary>
    [Fact]
    public async Task A_van_transfer_already_applied_is_not_counted_twice()
    {
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _sapTransfers.Add(Transfer(88901, "KEFGRC", Van, 24));
        await RefreshAsync(Van);

        var second = await RefreshAsync(Van);

        Assert.Equal(0, second.Value.TransfersApplied);
        Assert.Equal(1, second.Value.TransfersToday);
        Assert.Equal(1, _transfersDelivered);
        Assert.Equal(124m, await AvailableAsync(Van, Item));
    }

    /// <summary>
    /// The listener got there first. The press must leave its delivery alone.
    /// </summary>
    [Fact]
    public async Task A_transfer_the_listener_already_delivered_is_left_alone()
    {
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _sapTransfers.Add(Transfer(88901, "KEFGRC", Van, 24));
        await DeliverByListenerAsync(new ProcessTransferEventCommand(Item, "KEFGRC", Van, 24m, 88901, 88901));

        var result = await RefreshAsync(Van);

        Assert.Equal(0, result.Value.TransfersApplied);
        Assert.Equal(0, _transfersDelivered);
        Assert.Equal(124m, await AvailableAsync(Van, Item));
    }

    /// <summary>
    /// The listener delivered this one before the 07:00 fetch, so it is journalled against yesterday's
    /// ledger day and the morning figure already holds it. Applying it today would load it twice.
    /// </summary>
    [Fact]
    public async Task A_transfer_delivered_before_the_morning_fetch_is_not_applied_again()
    {
        await SeedRowAsync(Van, Item, original: 124m, available: 124m);
        _sapTransfers.Add(Transfer(88901, "KEFGRC", Van, 24));

        _context.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
        {
            SnapshotDate = LedgerDay.AddDays(-1),
            ItemCode = Item,
            WarehouseCode = Van,
            AdjustmentQuantity = 24m,
            Direction = UndeliveredTransfers.In,
            TransferDocEntry = 88901,
            TransferDocNum = 88901,
            SourceWarehouse = "KEFGRC",
            DestinationWarehouse = Van,
            DetectedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await RefreshAsync(Van);

        Assert.Equal(0, result.Value.TransfersApplied);
        Assert.Equal(0, _transfersDelivered);
        Assert.Equal(124m, await AvailableAsync(Van, Item));
    }

    /// <summary>
    /// The webhook keys on document, item and direction, so a document carrying the item on two lines
    /// arrives from the listener as one applied line and one skipped duplicate. Read from SAP the lines
    /// are summed first, so the whole quantity lands.
    /// </summary>
    [Fact]
    public async Task Two_lines_of_one_item_on_a_transfer_both_land()
    {
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _sapTransfers.Add(new SapStockMovementDocument
        {
            DocEntry = 88904,
            DocNum = 88904,
            FromWarehouse = "KEFGRC",
            ToWarehouse = Van,
            StockTransferLines =
            [
                new SapStockMovementLine { ItemCode = Item, Quantity = 10, FromWarehouseCode = "KEFGRC", WarehouseCode = Van },
                new SapStockMovementLine { ItemCode = Item, Quantity = 6, FromWarehouseCode = "KEFGRC", WarehouseCode = Van }
            ]
        });

        await RefreshAsync(Van);

        Assert.Equal(116m, await AvailableAsync(Van, Item));
    }

    [Fact]
    public async Task A_van_refresh_says_so_when_SAP_cannot_be_read()
    {
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _transferReadFailure = new HttpRequestException("No connection could be made");

        var result = await RefreshAsync(Van);

        Assert.True(result.IsError);
        Assert.Equal("Stock.SapUnavailable", result.FirstError.Code);
        Assert.Equal(100m, await AvailableAsync(Van, Item));
    }

    /// <summary>
    /// HttpClient's own timeout throws a cancellation with the caller's token untouched. That is SAP
    /// not answering, and must be said so rather than surfacing as a cancelled request.
    /// </summary>
    [Fact]
    public async Task A_SAP_read_that_times_out_is_reported_rather_than_thrown()
    {
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _transferReadFailure = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException());

        var result = await RefreshAsync(Van);

        Assert.True(result.IsError);
        Assert.Equal("Stock.SapUnavailable", result.FirstError.Code);
    }

    /// <summary>
    /// The SAP-correction switch governs the shop path only; a van's refresh reads no SAP figure.
    /// </summary>
    [Fact]
    public async Task A_van_refresh_does_not_depend_on_the_reconciliation_switch()
    {
        _settings.ReconcileLedgerAgainstSap = false;
        await SeedRowAsync(Van, Item, original: 100m, available: 100m);
        _sapTransfers.Add(Transfer(88902, Van, "KEFGRC", 10));

        var result = await RefreshAsync(Van);

        Assert.False(result.IsError);
        Assert.Equal(90m, await AvailableAsync(Van, Item));
    }

    /// <summary>
    /// No morning load to put transfers on: the van is fetched, exactly as a shop is — SAP's figure at
    /// the fetch already holds every transfer made so far.
    /// </summary>
    [Fact]
    public async Task A_van_with_no_snapshot_today_is_fetched()
    {
        var result = await RefreshAsync(Van);

        Assert.False(result.IsError);
        Assert.True(result.Value.SnapshotRefetched);
        Assert.False(result.Value.ByTransfer);
        Assert.Equal(0, _transfersDelivered);
    }

    /// <summary>
    /// A van added from Tracked warehouses is snapshotted every morning without being in
    /// appsettings.json. Its refresh has to go through, as VAN002's did not on 2026-09-29.
    /// </summary>
    [Fact]
    public async Task A_van_tracked_only_in_the_saved_list_is_refreshed()
    {
        const string SavedVan = "VAN002";
        await MonitoredWarehouseList.SaveAsync(_context, [Shop, Van, SavedVan], updatedByUserId: null);
        _context.ChangeTracker.Clear();
        await SeedRowAsync(SavedVan, Item, original: 100m, available: 100m);
        _sapTransfers.Add(Transfer(88903, "KEFGRC", SavedVan, 24));

        var result = await RefreshAsync(SavedVan);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.True(result.Value.ByTransfer);
        Assert.Equal(1, result.Value.TransfersApplied);
        Assert.Equal(124m, await AvailableAsync(SavedVan, Item));
    }

    // ---------------------------------------------------------------
    // Refusals — each one leaves the ledger exactly as it was
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_warehouse_that_is_not_monitored_is_refused()
    {
        var result = await RefreshAsync("KEFXXX");

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
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

    private async Task<ErrorOr<RefreshWarehouseStockResult>> RefreshAsync(
        string warehouse,
        bool stockReadsForbidden = false)
    {
        var sap = SapClient(stockReadsForbidden);

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
            Sender(),
            NullLogger<RefreshWarehouseStockHandler>.Instance);

        var result = await handler.Handle(new RefreshWarehouseStockCommand(warehouse), CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    /// <summary>
    /// MediatR as the handler sees it: the transfer command goes to the webhook's own handler, on the
    /// same context, as it does inside one request scope. The listener is not part of the van path.
    /// </summary>
    private ISender Sender() =>
        StubProxy.For<ISender>((method, args) =>
            method.Name == nameof(ISender.Send) && args?[0] is ProcessTransferEventCommand command
                ? DeliverThroughWebhookAsync(command)
                : throw new InvalidOperationException($"Unexpected sender call: {method.Name}"));

    private async Task<ErrorOr<ProcessTransferEventResult>> DeliverThroughWebhookAsync(ProcessTransferEventCommand command)
    {
        _transfersDelivered++;
        return await Webhook(_context).Handle(command, CancellationToken.None);
    }

    /// <summary>The listener's delivery: the webhook on a context of its own, as the separate request it is.</summary>
    private async Task DeliverByListenerAsync(ProcessTransferEventCommand command)
    {
        await using var webhookContext = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

        await Webhook(webhookContext).Handle(command, CancellationToken.None);
    }

    private ProcessTransferEventHandler Webhook(ApplicationDbContext context) => new(
        context,
        Options.Create(_settings),
        new CapturingPublisher(),
        NullLogger<ProcessTransferEventHandler>.Instance);

    private static SapStockMovementDocument Transfer(int docEntry, string from, string to, double quantity) => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry,
        FromWarehouse = from,
        ToWarehouse = to,
        StockTransferLines =
        [
            new SapStockMovementLine { ItemCode = Item, Quantity = quantity, FromWarehouseCode = from, WarehouseCode = to }
        ]
    };

    private ISAPServiceLayerClient SapClient(bool stockReadsForbidden) =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            // Read ahead of a shop's stock, and the only thing a van's refresh may read.
            nameof(ISAPServiceLayerClient.GetStockTransfersCreatedAsync) => _transferReadFailure is { } failure
                ? Task.FromException<List<SapStockMovementDocument>>(failure)
                : Task.FromResult(_sapTransfers.ToList()),

            _ when stockReadsForbidden => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} must not be read for a van."),

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
