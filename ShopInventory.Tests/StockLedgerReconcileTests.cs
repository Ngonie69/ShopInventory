using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The hourly comparison against SAP putting the ledger back in step, rather than only saying it is
/// out of step.
/// </summary>
/// <remarks>
/// On 2026-09-11 the job compared 93 items that had moved and recorded 89 divergences, and corrected
/// none of them — the ledger was wrong about nearly everything it had touched and stayed wrong for
/// the rest of the trading day. Both directions were live at shop tills: <c>ICS025/026/027</c> in
/// KEFGRS sat at zero against 17–20 in SAP, so the till refused sales the stock was there for, and
/// <c>VHU002</c> in KEFBYC stood 154 above SAP, which is the same fault pointed at the customer.
/// Those two shapes are the first two tests here.
///
/// <para>
/// The test that matters most is neither of them. It is
/// <see cref="An_unposted_till_sale_is_held_back_from_the_correction"/>: a till sale leaves the
/// counter before SAP hears about it, so copying SAP over the ledger would hand the same units back
/// to the shop floor and sell them twice. A reconciler that passes the first two tests and fails that
/// one is worse than no reconciler at all.
/// </para>
/// </remarks>
public sealed class StockLedgerReconcileTests : IDisposable
{
    private const string Shop = "KEFSHOP";
    private const string Van = "VAN001";
    private const string Item = "VHU002";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private readonly Dictionary<string, decimal> _sapIssuable = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BatchNumber> _sapBatches = new();
    private bool _batchReadFails;
    private string? _failStockReadFor;

    /// <summary>What the whole-warehouse reads answer, which is what arrival discovery searches.</summary>
    private readonly List<StockQuantityDto> _warehouseStock = new();
    private readonly List<BatchNumber> _warehouseBatches = new();

    private DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = [Shop, Van],
        ReconcileWarehouses = [Shop],
        ReconcileLedgerAgainstSap = true
    };

    public StockLedgerReconcileTests()
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

    // ---------------------------------------------------------------
    // The two shapes seen at shop tills on 2026-09-11
    // ---------------------------------------------------------------

    /// <summary>
    /// VHU002 in KEFBYC: ledger 4226, SAP 4072. The ledger was promising 154 units that had gone, and
    /// every one of them was a sale the till would take and the customer would not get.
    /// </summary>
    [Fact]
    public async Task A_ledger_promising_stock_SAP_does_not_have_is_drawn_down_to_SAP()
    {
        await SeedRowAsync(Shop, Item, original: 120m, available: 100m);
        _sapIssuable[Item] = 92m;

        await RunAsync();

        Assert.Equal(92m, await AvailableAsync(Shop, Item));
    }

    /// <summary>
    /// ICS025 in KEFGRS: ledger 0, SAP 20. The shop had the stock on the shelf and the till would not
    /// put it on a receipt — the half of this that looks like an item missing from the product list.
    /// </summary>
    [Fact]
    public async Task A_ledger_refusing_sales_SAP_could_cover_is_put_back_up_to_SAP()
    {
        await SeedRowAsync(Shop, Item, original: 15m, available: 0m, batch: "B1");
        _sapIssuable[Item] = 20m;
        _sapBatches.Add(Batch("B1", 20m));

        await RunAsync();

        Assert.Equal(20m, await AvailableAsync(Shop, Item));
    }

    // ---------------------------------------------------------------
    // Why SAP is not simply copied over
    // ---------------------------------------------------------------

    /// <summary>
    /// A till sale commits when the goods leave the counter and reaches SAP later, so in that window
    /// SAP is higher than the truth by exactly the unposted sales. Copying SAP's 100 over the ledger
    /// would put the 30 units that have already left the shop back on the shelf and sell them twice.
    /// </summary>
    [Fact]
    public async Task An_unposted_till_sale_is_held_back_from_the_correction()
    {
        await SeedRowAsync(Shop, Item, original: 120m, available: 90m);
        await SeedTillSaleAsync(Shop, Item, 30m, DesktopSaleConsolidationStatus.Pending);
        _sapIssuable[Item] = 100m;
        _sapBatches.Add(Batch("B1", 100m));

        await RunAsync();

        // 100 in SAP, less the 30 SAP has not been told about.
        Assert.Equal(70m, await AvailableAsync(Shop, Item));
    }

    /// <summary>
    /// The mirror of the test above, and the reason it cannot just subtract every till sale it finds:
    /// once a sale is consolidated SAP already holds it, so subtracting it again would take the same
    /// units off the ledger twice and refuse sales the shop could make.
    /// </summary>
    [Fact]
    public async Task A_till_sale_SAP_already_holds_is_not_subtracted_twice()
    {
        await SeedRowAsync(Shop, Item, original: 120m, available: 90m);
        await SeedTillSaleAsync(Shop, Item, 30m, DesktopSaleConsolidationStatus.Consolidated);
        _sapIssuable[Item] = 100m;
        _sapBatches.Add(Batch("B1", 100m));

        await RunAsync();

        Assert.Equal(100m, await AvailableAsync(Shop, Item));
    }

    // ---------------------------------------------------------------
    // What it will not touch
    // ---------------------------------------------------------------

    /// <summary>
    /// A van's row is its morning load, not a live figure — van sales only reach the ledger at end of
    /// day, so during trading it is meant to sit still while SAP moves beneath it. Correcting it
    /// would destroy the number the van reconciliation is computed from. Vans are the bulk of the
    /// divergences this job reports and none of them are faults.
    /// </summary>
    [Fact]
    public async Task A_van_warehouse_is_reported_and_left_alone()
    {
        await SeedRowAsync(Van, Item, original: 120m, available: 100m);
        _sapIssuable[Item] = 40m;

        await RunAsync();

        Assert.Equal(100m, await AvailableAsync(Van, Item));
        Assert.Single(await _context.StockLedgerDivergences.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The kill switch. With it off the job must behave exactly as it did before corrections existed:
    /// every divergence still recorded, not one of them acted on.
    /// </summary>
    [Fact]
    public async Task With_reconciliation_off_the_job_reports_and_changes_nothing()
    {
        _settings.ReconcileLedgerAgainstSap = false;

        await SeedRowAsync(Shop, Item, original: 120m, available: 100m);
        _sapIssuable[Item] = 92m;

        await RunAsync();

        Assert.Equal(100m, await AvailableAsync(Shop, Item));
        Assert.Single(await _context.StockLedgerDivergences.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Correcting does not replace reporting. The divergence row is the only record that something
    /// outside this system is moving stock, and a corrected ledger erases the evidence of it.
    /// </summary>
    [Fact]
    public async Task A_corrected_divergence_is_still_recorded()
    {
        await SeedRowAsync(Shop, Item, original: 120m, available: 100m);
        _sapIssuable[Item] = 92m;

        await RunAsync();

        var divergence = Assert.Single(await _context.StockLedgerDivergences.AsNoTracking().ToListAsync());
        Assert.Equal(Item, divergence.ItemCode);
        Assert.Equal(100m, divergence.LedgerQuantity);
        Assert.Equal(92m, divergence.SapIssuableQuantity);
        Assert.Equal(8m, divergence.Difference);
    }

    // ---------------------------------------------------------------
    // The bounds that make a bad SAP read harmless
    // ---------------------------------------------------------------

    /// <summary>
    /// Stock is only ever put back against a batch SAP names, up to the quantity SAP gives it. Asked
    /// for 50 with only 12 reported across the batches, it puts back 12 and leaves the rest.
    /// </summary>
    [Fact]
    public async Task Stock_is_never_put_back_beyond_what_SAP_reports_on_the_batch()
    {
        await SeedRowAsync(Shop, Item, original: 15m, available: 0m, batch: "B1");
        _sapIssuable[Item] = 50m;
        _sapBatches.Add(Batch("B1", 12m));

        await RunAsync();

        Assert.Equal(12m, await AvailableAsync(Shop, Item));
    }

    /// <summary>
    /// SAP refuses a batch-managed line that names no batch, and takes the whole document with it. So
    /// a batch read that comes back empty for an item whose every row carries a batch is a degraded
    /// read, not an unbatched item, and inventing a batchless row would put stock on the till that no
    /// document could ever take off it.
    /// </summary>
    [Fact]
    public async Task A_failed_batch_read_never_invents_batchless_stock_for_a_batched_item()
    {
        await SeedRowAsync(Shop, Item, original: 15m, available: 0m, batch: "B1");
        _sapIssuable[Item] = 20m;
        _batchReadFails = true;

        await RunAsync();

        Assert.Equal(0m, await AvailableAsync(Shop, Item));
        Assert.Empty(await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.BatchNumber == null)
            .ToListAsync());
    }

    /// <summary>
    /// Most of the item master is not batch managed — the 117 items in Complimentary Products carry
    /// no batch records at all — and the morning fetch writes those as a single row with no batch
    /// number. An item SAP reports no batches for is therefore normal, and is grown the same way.
    /// </summary>
    [Fact]
    public async Task An_unbatched_item_is_put_back_on_its_batchless_row()
    {
        await SeedRowAsync(Shop, Item, original: 15m, available: 2m);
        _sapIssuable[Item] = 18m;

        await RunAsync();

        Assert.Equal(18m, await AvailableAsync(Shop, Item));
    }

    /// <summary>
    /// A warehouse already negative in SAP has nothing left to promise. Left signed, the target would
    /// read as stock owed rather than stock absent and the arithmetic downstream would take it as a
    /// figure to sell against.
    /// </summary>
    [Fact]
    public async Task A_warehouse_negative_in_SAP_is_floored_at_nothing_left_to_promise()
    {
        await SeedRowAsync(Shop, Item, original: 15m, available: 10m);
        _sapIssuable[Item] = -52m;

        await RunAsync();

        Assert.Equal(0m, await AvailableAsync(Shop, Item));
    }

    /// <summary>
    /// Drawing down follows expiry order, the order the till deducts in and the order SAP's FEFO
    /// allocation picks. Taking from the wrong end leaves the ledger holding batches the shop has
    /// already sold through.
    /// </summary>
    [Fact]
    public async Task Drawing_down_takes_from_the_soonest_expiring_batch_first()
    {
        await SeedRowAsync(Shop, Item, original: 6m, available: 5m, batch: "OLD",
            expiry: new DateTime(2026, 8, 1));
        await SeedRowAsync(Shop, Item, original: 6m, available: 5m, batch: "NEW",
            expiry: new DateTime(2026, 12, 1));
        _sapIssuable[Item] = 6m;

        await RunAsync();

        var rows = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.WarehouseCode == Shop)
            .OrderBy(row => row.ExpiryDate)
            .ToListAsync();

        // Ten held against six in SAP, so four come off — all of them from the batch that expires
        // first. Drawing from the other end would read 5 and 1.
        Assert.Equal(1m, rows[0].AvailableQuantity);
        Assert.Equal(5m, rows[1].AvailableQuantity);
    }

    /// <summary>
    /// A row still at its morning figure has had nothing happen to it this system knows about, and
    /// asking SAP about every one of them is the whole-warehouse scan that starves the six process-
    /// wide SAP slots. The restraint is real and so is its cost, which is why it is pinned here.
    /// </summary>
    [Fact]
    public async Task An_untouched_row_is_not_compared_and_not_corrected()
    {
        await SeedRowAsync(Shop, Item, original: 100m, available: 100m);
        _sapIssuable[Item] = 40m;

        await RunAsync();

        Assert.Equal(100m, await AvailableAsync(Shop, Item));
        Assert.Empty(await _context.StockLedgerDivergences.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// One warehouse failing must not take the rest of the run with it — including its reporting.
    /// </summary>
    /// <remarks>
    /// The report is the thing worth keeping on a bad pass, so it is the thing asserted here: a
    /// warehouse SAP would not answer for, followed by one it would, and the second one's divergence
    /// still recorded and still acted on.
    ///
    /// <para>
    /// What this does <i>not</i> reach is the harder half of the same problem. The two things this
    /// job writes share one database context, so a snapshot row EF cannot save is retried on every
    /// later save — which would take down the next warehouse's write and, at the end, the divergence
    /// rows for every warehouse that had succeeded. <c>DetachSnapshotRows</c> is what lets go of
    /// them. Reaching it needs a <c>DbUpdateConcurrencyException</c>, and this suite cannot raise
    /// one: <c>SnapshotSqliteContext</c> turns the row's concurrency token off outright, which is why
    /// <c>StockLedgerTests</c> records the ledger's own retry-on-conflict path as unreachable here
    /// too. Removing both detach calls leaves this test green. It is covered by reasoning and by the
    /// note on the method, not by this file.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_warehouse_whose_SAP_read_fails_does_not_cost_the_next_one_its_report()
    {
        _settings.MonitoredWarehouses = [Van, Shop];
        _settings.ReconcileWarehouses = [Shop];
        _failStockReadFor = Van;

        await SeedRowAsync(Van, Item, original: 120m, available: 100m);
        await SeedRowAsync(Shop, Item, original: 120m, available: 100m);
        _sapIssuable[Item] = 92m;

        await RunAsync();

        var divergence = Assert.Single(await _context.StockLedgerDivergences.AsNoTracking().ToListAsync());
        Assert.Equal(Shop, divergence.WarehouseCode);
        Assert.Equal(92m, await AvailableAsync(Shop, Item));
    }

    // ---------------------------------------------------------------
    // Stock that arrived with no snapshot row at all
    // ---------------------------------------------------------------

    /// <summary>
    /// The residual gap everything else leaves open. The comparison can only ask about rows that
    /// moved, so an item the warehouse held none of at 07:00 is invisible to it — and a goods receipt
    /// or production receipt booked straight into SAP brings no row of its own the way a transfer
    /// does. The shop sees the item on the shelf and the till does not list it.
    /// </summary>
    [Fact]
    public async Task Stock_that_arrived_with_no_snapshot_row_is_added_to_the_ledger()
    {
        await SeedSnapshotAsync(Shop);
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));

        await RunAsync();

        Assert.Equal(24m, await AvailableAsync(Shop, "NEW001"));
    }

    /// <summary>
    /// SAP refuses a batch-managed line that names no batch and takes the whole document with it, so
    /// an arrival has to carry the batch it actually sits in. Composed through the same
    /// <c>SnapshotComposer</c> the morning fetch uses, so the row is the shape a till can sell.
    /// </summary>
    [Fact]
    public async Task A_batch_managed_arrival_carries_its_batch_and_expiry()
    {
        await SeedSnapshotAsync(Shop);
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));
        _warehouseBatches.Add(Batch("B9", 24m, new DateTime(2026, 11, 30), itemCode: "NEW001"));

        await RunAsync();

        var row = Assert.Single(await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(candidate => candidate.ItemCode == "NEW001")
            .ToListAsync());

        Assert.Equal("B9", row.BatchNumber);
        Assert.Equal(new DateTime(2026, 11, 30), row.ExpiryDate);
        Assert.Equal(24m, row.AvailableQuantity);
    }

    /// <summary>
    /// A newly arrived item can already have been sold, if it reached the shelf before it reached
    /// here. Those sales are not in SAP yet, so the same subtraction the comparison makes applies.
    /// </summary>
    [Fact]
    public async Task An_arrival_already_sold_from_is_added_net_of_the_unposted_sales()
    {
        await SeedSnapshotAsync(Shop);
        await SeedTillSaleAsync(Shop, "NEW001", 4m, DesktopSaleConsolidationStatus.Pending);
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));

        await RunAsync();

        Assert.Equal(20m, await AvailableAsync(Shop, "NEW001"));
    }

    /// <summary>
    /// An item that already has a row belongs to the comparison, which knows what has been promised
    /// against it. Adding a second row here would correct the same item twice by two routes in one
    /// pass.
    /// </summary>
    [Fact]
    public async Task An_item_that_already_has_a_row_is_left_to_the_comparison()
    {
        await SeedRowAsync(Shop, Item, original: 120m, available: 100m);
        _sapIssuable[Item] = 92m;
        _warehouseStock.Add(Stock(Item, inStock: 92m));

        await RunAsync();

        var rows = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.ItemCode == Item)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal(92m, rows[0].AvailableQuantity);
    }

    /// <summary>
    /// Without a finished snapshot the ledger reports the warehouse as untracked, and a till is told
    /// there are no figures rather than sold against a partial set. Adding rows under a header that
    /// does not exist would put stock on a till the rest of the system says it cannot sell from.
    /// </summary>
    [Fact]
    public async Task Nothing_is_added_to_a_warehouse_with_no_finished_snapshot()
    {
        await SeedSnapshotAsync(Shop, StockSnapshotStatus.Pending);
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));

        await RunAsync();

        Assert.Empty(await _context.DailyStockSnapshotItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task With_arrival_discovery_off_nothing_is_added()
    {
        _settings.DiscoverNewStockArrivals = false;

        await SeedSnapshotAsync(Shop);
        _warehouseStock.Add(Stock("NEW001", inStock: 24m));

        await RunAsync();

        Assert.Empty(await _context.DailyStockSnapshotItems.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The rotation is the throttle. Each warehouse costs the pair of whole-warehouse reads the
    /// morning fetch makes, so a pass takes a slice rather than all of them — but every warehouse has
    /// to come round, or one shop is never searched and nothing says so.
    /// </summary>
    [Fact]
    public void The_arrival_search_rotates_over_every_warehouse()
    {
        string[] warehouses = ["A", "B", "C", "D", "E", "F", "G"];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var start = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
        for (var hour = 0; hour < warehouses.Length; hour++)
        {
            foreach (var warehouse in StockLedgerDivergenceJob.DiscoverySlots(
                         warehouses, perPass: 2, start.AddHours(hour)))
            {
                seen.Add(warehouse);
            }
        }

        Assert.Equal(warehouses.Length, seen.Count);
    }

    [Fact]
    public void The_arrival_search_takes_no_warehouse_when_it_is_given_none()
    {
        Assert.Empty(StockLedgerDivergenceJob.DiscoverySlots([], perPass: 2, DateTime.UtcNow));
        Assert.Empty(StockLedgerDivergenceJob.DiscoverySlots(["A"], perPass: 0, DateTime.UtcNow));
    }

    // ── Helpers ─────────────────────────────────────────

    private DateTime LedgerDay => StockLedgerDay.Today(_settings.StockFetchTimeCAT);

    private async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(SapClient());
        services.AddSingleton<IStockLedger>(new StockLedger(
            _context, Options.Create(_settings), NullLogger<StockLedger>.Instance));

        var job = new StockLedgerDivergenceJob(
            services.BuildServiceProvider(),
            Options.Create(_settings),
            NullLogger<StockLedgerDivergenceJob>.Instance);

        await job.Execute(JobContext());
        _context.ChangeTracker.Clear();
    }

    private ISAPServiceLayerClient SapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                string.Equals((string)args![0]!, _failStockReadFor, StringComparison.OrdinalIgnoreCase)
                    ? throw new InvalidOperationException("SAP stock read failed")
                    : Task.FromResult(((IEnumerable<string>)args[1]!)
                    .Where(_sapIssuable.ContainsKey)
                    .Select(itemCode => new StockQuantityDto
                    {
                        ItemCode = itemCode,
                        WarehouseCode = (string)args[0]!,
                        // Issuable is InStock less Committed, and it is Issuable the job compares.
                        InStock = _sapIssuable[itemCode],
                        Committed = 0m
                    })
                    .ToList()),

            nameof(ISAPServiceLayerClient.GetAllBatchNumbersInWarehouseAsync) =>
                Task.FromResult(_warehouseBatches.ToList()),

            nameof(ISAPServiceLayerClient.GetStockQuantitiesInWarehouseAsync) =>
                Task.FromResult(_warehouseStock.ToList()),

            nameof(ISAPServiceLayerClient.GetBatchNumbersForItemsInWarehouseAsync) =>
                _batchReadFails
                    ? throw new InvalidOperationException("SAP batch read failed")
                    : Task.FromResult(_sapBatches.ToList()),

            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private static IJobExecutionContext JobContext() =>
        StubProxy.For<IJobExecutionContext>((method, _) => method.Name switch
        {
            "get_CancellationToken" => CancellationToken.None,
            _ => throw new InvalidOperationException(
                $"IJobExecutionContext.{method.Name} was not expected on this path.")
        });

    private static StockQuantityDto Stock(string itemCode, decimal inStock, decimal committed = 0m) => new()
    {
        ItemCode = itemCode,
        ItemName = itemCode,
        WarehouseCode = Shop,
        InStock = inStock,
        Committed = committed
    };

    private BatchNumber Batch(
        string batchNum, decimal quantity, DateTime? expiry = null, string? itemCode = null) => new()
    {
        ItemCode = itemCode ?? Item,
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

    private async Task SeedSnapshotAsync(
        string warehouse,
        StockSnapshotStatus status = StockSnapshotStatus.Complete)
    {
        _context.DailyStockSnapshots.Add(new DailyStockSnapshotEntity
        {
            SnapshotDate = LedgerDay,
            WarehouseCode = warehouse,
            Status = status
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedRowAsync(
        string warehouse,
        string itemCode,
        decimal original,
        decimal available,
        string? batch = null,
        DateTime? expiry = null)
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
            ExpiryDate = expiry,
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
            // Any instant inside the current ledger day. UtcNow always is one, whatever the hour:
            // the day runs from the fetch time in CAT, which is UtcNow's own window by construction.
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
