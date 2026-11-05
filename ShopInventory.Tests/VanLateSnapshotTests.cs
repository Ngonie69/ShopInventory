using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van's snapshot read after it has started its day still opens at what the van started the day with.
/// </summary>
/// <remarks>
/// On 2026-11-04 SAP failed the 07:00 read for every van and they were read at 10:50. SAP's figure then
/// held the morning's loads and had the posted sales taken off, and everything that reads a van's day
/// adds the loads and takes off the sales again. See <see cref="VanMorningOpening"/>.
/// </remarks>
public sealed class VanLateSnapshotTests : IDisposable
{
    private const string Van = "VAN006";
    private const string Shop = "KEFGRC";
    private const string Yoghurt = "YOG053";
    private const string Milk = "DAI009";

    /// <summary>A fixed trading day, so the CAT window the sales fall in never depends on when this runs.</summary>
    private static readonly DateTime Day = new(2026, 11, 4);

    /// <summary>08:00 CAT on <see cref="Day"/>.</summary>
    private static readonly DateTime MorningUtc = Day.AddHours(6);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Dictionary<string, List<BatchNumber>> _batches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SapStockMovementDocument> _sapTransfers = [];
    private readonly List<string> _sapCalls = [];
    private Exception? _transferReadFailure;
    private int _saleNumber;

    private readonly DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = [Shop, Van],
        ReconcileWarehouses = [Shop]
    };

    public VanLateSnapshotTests()
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

    /// <summary>
    /// VAN006 on 2026-11-04, simplified: it opened with 10, loaded 20 at 07:13, sold 5 that posted and 3
    /// that had not by the 10:50 read, when SAP said 25.
    /// </summary>
    [Fact]
    public async Task A_van_read_late_opens_at_what_it_started_the_day_with()
    {
        await GivenLoadRecordedAsync(Yoghurt, 20m, docEntry: 136298);
        await GivenSaleAsync(Yoghurt, 5m, postedToSap: true);
        await GivenSaleAsync(Yoghurt, 3m, postedToSap: false);
        GivenSapHolds(Van, (Yoghurt, "B-1", 25m));

        await FetchAsync(Van);

        var row = Assert.Single(await RowsAsync(Van));
        Assert.Equal(10m, row.OriginalQuantity);
        Assert.Equal(25m, row.AvailableQuantity);

        // 10 + 20 - 8: SAP's 25 less the 3 it has not been given. Read as SAP's 25, it was 37.
        Assert.Equal(22m, await OnTheVanAsync(Yoghurt));
    }

    [Fact]
    public async Task A_load_sap_booked_before_the_read_that_the_listener_has_not_delivered_counts_once()
    {
        _sapTransfers.Add(Transfer(136298, Shop, Van, Yoghurt, 20));
        GivenSapHolds(Van, (Yoghurt, "B-1", 30m));

        await FetchAsync(Van);

        // Recorded under the webhook's own key, so its later delivery is skipped as a duplicate.
        var adjustment = Assert.Single(await _context.StockTransferAdjustments.AsNoTracking().ToListAsync());
        Assert.Equal(136298, adjustment.TransferDocEntry);
        Assert.Equal(UndeliveredTransfers.In, adjustment.Direction);
        Assert.Equal(Day, adjustment.SnapshotDate);

        Assert.Equal(10m, Assert.Single(await RowsAsync(Van)).OriginalQuantity);
        Assert.Equal(30m, await OnTheVanAsync(Yoghurt));
    }

    [Fact]
    public async Task A_load_already_delivered_is_not_recorded_a_second_time()
    {
        await GivenLoadRecordedAsync(Yoghurt, 20m, docEntry: 136298);
        _sapTransfers.Add(Transfer(136298, Shop, Van, Yoghurt, 20));
        GivenSapHolds(Van, (Yoghurt, "B-1", 30m));

        await FetchAsync(Van);

        Assert.Equal(1, await _context.StockTransferAdjustments.CountAsync());
        Assert.Equal(30m, await OnTheVanAsync(Yoghurt));
    }

    /// <summary>The normal morning: nothing loaded or sold yet, so the opening is SAP's figure as read.</summary>
    [Fact]
    public async Task A_van_read_before_it_has_moved_opens_at_sap_figure()
    {
        GivenSapHolds(Van, (Yoghurt, "B-1", 12m), (Yoghurt, "B-2", 8m));

        await FetchAsync(Van);

        var rows = await RowsAsync(Van);
        Assert.Equal(20m, rows.Sum(row => row.OriginalQuantity));
        Assert.All(rows, row => Assert.Equal(row.AvailableQuantity, row.OriginalQuantity));
    }

    [Fact]
    public async Task An_item_sold_out_by_the_late_read_still_opens_with_what_was_sold()
    {
        await GivenSaleAsync(Milk, 4m, postedToSap: true);
        GivenSapHolds(Van, (Yoghurt, "B-1", 6m));

        await FetchAsync(Van);

        var milk = Assert.Single(await RowsAsync(Van), row => row.ItemCode == Milk);
        Assert.Equal(4m, milk.OriginalQuantity);
        Assert.Equal(0m, milk.AvailableQuantity);
        Assert.Equal(0m, await OnTheVanAsync(Milk));
    }

    /// <summary>
    /// The day's records and SAP disagree. The opening cannot be negative, and the snapshot is still
    /// written rather than failing the van's day over one item.
    /// </summary>
    [Fact]
    public async Task Loads_beyond_what_sap_holds_leave_the_opening_at_zero()
    {
        await GivenLoadRecordedAsync(Yoghurt, 50m, docEntry: 136298);
        GivenSapHolds(Van, (Yoghurt, "B-1", 25m));

        await FetchAsync(Van);

        Assert.Equal(StockSnapshotStatus.Complete, await StatusAsync(Van));
        Assert.Equal(0m, Assert.Single(await RowsAsync(Van)).OriginalQuantity);
    }

    [Fact]
    public async Task A_failed_read_of_sap_transfers_still_opens_the_van()
    {
        _transferReadFailure = new TimeoutException("Service Layer did not answer");
        await GivenLoadRecordedAsync(Yoghurt, 20m, docEntry: 136298);
        GivenSapHolds(Van, (Yoghurt, "B-1", 25m));

        await FetchAsync(Van);

        Assert.Equal(5m, Assert.Single(await RowsAsync(Van)).OriginalQuantity);
    }

    /// <summary>
    /// A shop's opening is SAP's figure: its loads are SAP's, its sales are netted by the fetch itself,
    /// and the hourly reconciliation corrects it. Nothing here is for shops.
    /// </summary>
    [Fact]
    public async Task A_shop_is_opened_at_sap_figure_and_its_transfers_are_not_read()
    {
        await GivenLoadRecordedAsync(Yoghurt, 20m, docEntry: 136298, warehouse: Shop);
        GivenSapHolds(Shop, (Yoghurt, "B-1", 40m));

        await FetchAsync(Shop);

        Assert.Equal(40m, Assert.Single(await RowsAsync(Shop)).OriginalQuantity);
        Assert.DoesNotContain(nameof(ISAPServiceLayerClient.GetStockTransfersCreatedAsync), _sapCalls);
    }

    // ── Helpers ─────────────────────────────────────────

    private async Task FetchAsync(string warehouse)
    {
        var handler = new FetchDailyStockHandler(
            _context,
            Sap(),
            StubProxy.Unused<IHubContext<NotificationHub>>(),
            Options.Create(_settings),
            StubProxy.Unused<ITransferEventListenerClient>(),
            Options.Create(new TransferEventListenerSettings()),
            new StockFetchGate(),
            NullLogger<FetchDailyStockHandler>.Instance);

        var result = await handler.FetchWarehouseStockAsync(Day, warehouse, CancellationToken.None);
        Assert.Equal("Complete", result.Status);

        _context.ChangeTracker.Clear();
    }

    private async Task<decimal> OnTheVanAsync(string itemCode)
    {
        var reading = await VanStockPosition.ReadAsync(_context, Van, Day, leaveOutSalesStillHeld: false, default);
        Assert.True(reading.Counted);
        return Assert.Single(reading.Items, item => item.ItemCode == itemCode).Quantity;
    }

    private Task<List<DailyStockSnapshotItemEntity>> RowsAsync(string warehouse) =>
        _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.WarehouseCode == warehouse)
            .ToListAsync();

    private async Task<StockSnapshotStatus> StatusAsync(string warehouse) =>
        (await _context.DailyStockSnapshots.AsNoTracking().SingleAsync(header => header.WarehouseCode == warehouse))
        .Status;

    private async Task GivenLoadRecordedAsync(string itemCode, decimal quantity, int docEntry, string warehouse = Van)
    {
        _context.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
        {
            SnapshotDate = Day,
            ItemCode = itemCode,
            WarehouseCode = warehouse,
            AdjustmentQuantity = quantity,
            Direction = UndeliveredTransfers.In,
            TransferDocEntry = docEntry,
            TransferDocNum = docEntry,
            SourceWarehouse = "KEFGRC",
            DestinationWarehouse = warehouse,
            DetectedAt = MorningUtc.AddMinutes(-47)
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenSaleAsync(string itemCode, decimal quantity, bool postedToSap)
    {
        var number = ++_saleNumber;

        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"{Van}-INV-{number}",
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = Van,
            WarehouseCode = Van,
            Currency = "USD",
            DocDate = Day,
            CreatedAt = MorningUtc.AddMinutes(number),
            SapDocEntry = postedToSap ? 2419000 + number : null,
            PostedAt = postedToSap ? MorningUtc.AddMinutes(number + 1) : null,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 1, ItemCode = itemCode, Quantity = quantity, UnitPrice = 1m, LineTotal = quantity,
                    WarehouseCode = Van
                }
            ]
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private void GivenSapHolds(string warehouse, params (string ItemCode, string Batch, decimal Quantity)[] batches) =>
        _batches[warehouse] = batches
            .Select(batch => new BatchNumber
            {
                ItemCode = batch.ItemCode,
                ItemName = batch.ItemCode,
                BatchNum = batch.Batch,
                Quantity = batch.Quantity,
                ExpiryDate = "2026-12-01"
            })
            .ToList();

    private static SapStockMovementDocument Transfer(int docEntry, string from, string to, string itemCode, double quantity) => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry,
        FromWarehouse = from,
        ToWarehouse = to,
        StockTransferLines =
        [
            new SapStockMovementLine { ItemCode = itemCode, Quantity = quantity, FromWarehouseCode = from, WarehouseCode = to }
        ]
    };

    private ISAPServiceLayerClient Sap() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            _sapCalls.Add(method.Name);
            var warehouse = args is { Length: > 0 } && args[0] is string code ? code : null;

            return method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetStockTransfersCreatedAsync) => _transferReadFailure is { } failure
                    ? Task.FromException<List<SapStockMovementDocument>>(failure)
                    : Task.FromResult(_sapTransfers.ToList()),

                nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) =>
                    Task.FromResult(_batches.GetValueOrDefault(warehouse!, []).ToList()),

                nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                    Task.FromResult(_batches.GetValueOrDefault(warehouse!, [])
                        .GroupBy(batch => batch.ItemCode)
                        .Select(group => new StockQuantityDto
                        {
                            ItemCode = group.Key,
                            ItemName = group.Key,
                            WarehouseCode = warehouse,
                            InStock = group.Sum(batch => batch.Quantity)
                        })
                        .ToList()),

                nameof(ISAPServiceLayerClient.GetNonBatchStockQuantitiesInWarehouseAsync) =>
                    Task.FromResult(new List<StockQuantityDto>()),

                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            };
        });
}
