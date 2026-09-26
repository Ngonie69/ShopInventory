using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the van stock report.
///
/// Each morning's count is SAP's book stock, so it is explained by the SAP documents created since
/// the count before it — placed by when SAP created them, never by the date printed on them or by the
/// app's own trading day. That is the rule these tests hold: a day's sales invoiced after the next
/// count must read as late, not as stock missing one morning and found the next. And the snapshot's
/// <c>AvailableQuantity</c> is never read as what is left on a van: no van sales path decrements it.
/// </summary>
public sealed class VanStockReportTests : IDisposable
{
    private const string Van = "VAN010";
    private const string Depot = "KEFGRC";

    private static readonly Guid Rep = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTime From = new(2026, 8, 1);
    private static readonly DateTime To = new(2026, 8, 31);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly FakeSapDocuments _sap = new();

    public VanStockReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();


        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van010",
            Email = "van010@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = Van,
            SupplyingWarehouseCode = Depot,
            AssignedBusinessPartnerCode = Van
        });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- C1: the day ---

    /// <summary>
    /// The load is the snapshot's morning quantity and what sold comes from the sales themselves —
    /// never from the snapshot's running column, which no van path maintains.
    /// </summary>
    [Fact]
    public async Task A_days_load_and_sales_produce_the_expected_remaining()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m), ("NRI049", 50m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        await _context.SaveChangesAsync();

        var day = Assert.Single((await RunAsync()).Days);

        Assert.Equal(150m, day.LoadedQuantity);
        Assert.Equal(60m, day.SoldQuantity);
        Assert.Equal(90m, day.ExpectedRemaining);
        Assert.Equal(1, day.SoldItemCount);
        Assert.Equal(1, day.UnsoldItemCount);
        Assert.Equal(0.4, day.SellThroughRate!.Value, 3);
    }

    /// <summary>
    /// Snapshot rows are per batch, so an item is the sum of its batches. Counting rows instead
    /// would report a two-batch item as two items and halve the sell-through.
    /// </summary>
    [Fact]
    public async Task Batches_of_one_item_are_summed_into_one_item()
    {
        AddSnapshotBatches(new DateTime(2026, 8, 4),
            ("CHE011", 60m, "BATCH-A"),
            ("CHE011", 40m, "BATCH-B"));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 25m));
        await _context.SaveChangesAsync();

        var day = Assert.Single((await RunAsync()).Days);

        Assert.Equal(1, day.ItemCount);
        Assert.Equal(100m, day.LoadedQuantity);
        Assert.Equal(25m, day.SoldQuantity);
    }

    /// <summary>
    /// Selling more than was loaded is possible — stock can reach a van mid-round — but it means the
    /// load did not cover the day, and that is worth seeing rather than smoothing away.
    /// </summary>
    [Fact]
    public async Task Selling_beyond_the_load_is_flagged_rather_than_hidden()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 10m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 25m));
        await _context.SaveChangesAsync();

        var day = Assert.Single((await RunAsync()).Days);

        Assert.True(day.SoldBeyondLoad);
        Assert.Equal(-15m, day.ExpectedRemaining);
    }

    // --- C2: each morning against SAP's documents ---

    /// <summary>
    /// The reconciliation itself. The count is SAP's book stock, so the invoice SAP created between
    /// the two counts is what explains the change.
    /// </summary>
    [Fact]
    public async Task A_morning_explained_by_sap_documents_ties()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 40m));
        _sap.Invoice(779001, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4, 16, 5, 0), ("CHE011", 60m));
        await _context.SaveChangesAsync();

        var morning = Assert.Single((await RunAsync()).Mornings);

        Assert.True(morning.SapChecked);
        Assert.True(morning.TiesToSap);
        Assert.Equal(0, morning.ItemsUnexplained);
        Assert.Equal(1, morning.ItemsMoved);

        var document = Assert.Single(morning.Documents);
        Assert.Equal("Invoice", document.Kind);
        Assert.Equal(779001, document.DocNum);
        Assert.Equal(0, document.DaysBackdated);
    }

    /// <summary>
    /// The case the report was rebuilt for, taken from VAN001 in September 2026: a day's sales invoiced
    /// in SAP after the next morning's count. SAP's stock did not move until the invoice was created,
    /// so the morning after the trading day ties with no document, and the morning after that ties
    /// with the invoice. The day is reported as late — not as stock missing and then found.
    /// </summary>
    [Fact]
    public async Task A_day_invoiced_after_the_next_count_is_late_not_missing_then_found()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 100m));
        AddSnapshot(new DateTime(2026, 8, 6), ("CHE011", 40m));
        // Dated the 4th, created at 09:22 on the 5th — after that morning's 07:00 count.
        _sap.Invoice(779350, new DateTime(2026, 8, 4), new DateTime(2026, 8, 5, 9, 22, 0), ("CHE011", 60m));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Mornings.Count);
        Assert.All(report.Mornings, morning => Assert.True(morning.TiesToSap));
        Assert.Empty(report.Mornings[0].Documents);

        var document = Assert.Single(report.Mornings[1].Documents);
        Assert.Equal(1, document.DaysBackdated);

        var day = Assert.Single(report.SalesDays);
        Assert.Equal("Late", day.Status);
        Assert.Equal(1, day.DaysLate);
        Assert.Equal(new DateTime(2026, 8, 5, 7, 0, 0), day.NextCountAt);
        Assert.Empty(day.Differences);

        var van = Assert.Single(report.Vans!);
        Assert.Equal(2, van.MorningsTied);
        Assert.Equal(1, van.SalesDaysLate);
        Assert.Equal(1, van.MaxDaysLate);
    }

    [Fact]
    public async Task A_day_invoiced_before_the_next_count_is_on_time()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 40m));
        _sap.Invoice(779001, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4, 18, 30, 0), ("CHE011", 60m));
        await _context.SaveChangesAsync();

        var day = Assert.Single((await RunAsync()).SalesDays);

        Assert.Equal("OnTime", day.Status);
        Assert.Null(day.DaysLate);
        Assert.Equal(1, day.SapInvoiceCount);
    }

    /// <summary>
    /// SAP moved stock by something other than an invoice, credit note or transfer — a breakage
    /// issued, a count posted. The report names the item and the amount rather than calling it lost.
    /// </summary>
    [Fact]
    public async Task Stock_moved_by_a_posting_the_report_does_not_read_is_unexplained()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m), ("NRI049", 50m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 91m), ("NRI049", 50m));
        await _context.SaveChangesAsync();

        var morning = Assert.Single((await RunAsync()).Mornings);

        Assert.False(morning.TiesToSap);
        Assert.Equal(2, morning.ItemCount);
        Assert.Equal(1, morning.ItemsUnexplained);

        var item = Assert.Single(morning.Unexplained);
        Assert.Equal("CHE011", item.ItemCode);
        Assert.Equal(100m, item.Expected);
        Assert.Equal(91m, item.Closing);
        Assert.Equal(-9m, item.Unexplained);
    }

    /// <summary>Loads on and off, and a customer's return, are all SAP documents and all explain the count.</summary>
    [Fact]
    public async Task Transfers_and_credit_notes_are_part_of_the_arithmetic()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m), ("PIC003", 20m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 125m), ("PIC003", 12m));
        _sap.Invoice(779001, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4, 16, 0, 0), ("CHE011", 20m));
        _sap.CreditNote(51001, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4, 17, 0, 0), "CHE011", 5m);
        _sap.TransferIn(88892, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4), "CHE011", 40m);
        _sap.TransferOut(88893, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4), "PIC003", 8m);
        await _context.SaveChangesAsync();

        var morning = Assert.Single((await RunAsync()).Mornings);

        Assert.True(morning.TiesToSap);
        Assert.Equal(4, morning.Documents.Count);
        Assert.Contains(morning.Documents, document => document.Kind == "TransferOut" && !document.CreatedTimeKnown);
    }

    /// <summary>
    /// A cancelled transfer arrives from SAP as the same transfer with negative quantities (88939,
    /// cancelled by 88940). The pair nets to nothing.
    /// </summary>
    [Fact]
    public async Task A_cancelled_transfer_nets_to_nothing()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 100m));
        _sap.TransferIn(88939, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4), "CHE011", 30m);
        _sap.TransferIn(88940, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4), "CHE011", -30m);
        await _context.SaveChangesAsync();

        var morning = Assert.Single((await RunAsync()).Mornings);

        Assert.True(morning.TiesToSap);
        Assert.Equal(2, morning.Documents.Count);
    }

    /// <summary>
    /// SAP stamps a transfer with a date and no time. When TransferEventListener saw it before the
    /// 07:00 count, that moment places it — at midday it would land in the next morning and make both
    /// mornings look wrong.
    /// </summary>
    [Fact]
    public async Task A_transfer_the_listener_saw_before_the_count_belongs_to_that_morning()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 130m));
        AddSnapshot(new DateTime(2026, 8, 6), ("CHE011", 130m));
        _sap.TransferIn(89001, new DateTime(2026, 8, 5), new DateTime(2026, 8, 5), "CHE011", 30m);
        // 04:30 UTC is 06:30 CAT, before the 5th's count.
        AddAdjustment(new DateTime(2026, 8, 4), "CHE011", 30m, docEntry: 89001,
            detectedAtUtc: new DateTime(2026, 8, 5, 4, 30, 0));
        await _context.SaveChangesAsync();

        var mornings = (await RunAsync()).Mornings;

        Assert.All(mornings, morning => Assert.True(morning.TiesToSap));
        var document = Assert.Single(mornings[0].Documents);
        Assert.True(document.CreatedTimeKnown);
        Assert.Equal(new DateTime(2026, 8, 5, 6, 30, 0), document.CreatedAt);
    }

    /// <summary>
    /// A missing count no longer breaks the chain. The documents cover every day between the two
    /// counts, so the comparison is over the longer window and nothing lands on the wrong date.
    /// </summary>
    [Fact]
    public async Task A_missing_count_is_bridged_by_the_documents_in_between()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        // Nothing on the 5th.
        AddSnapshot(new DateTime(2026, 8, 6), ("CHE011", 10m));
        _sap.Invoice(779001, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4, 16, 0, 0), ("CHE011", 60m));
        _sap.Invoice(779002, new DateTime(2026, 8, 5), new DateTime(2026, 8, 5, 16, 0, 0), ("CHE011", 30m));
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var morning = Assert.Single(report.Mornings);

        Assert.True(morning.HasGap);
        Assert.Equal(2, morning.GapDays);
        Assert.True(morning.TiesToSap);
        Assert.Equal(2, morning.Documents.Count);

        Assert.Equal(1, report.Quality.MissingSnapshotDays);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("across the gap"));
    }

    /// <summary>An unfinished count is missing items, so it is left out of the chain rather than compared.</summary>
    [Fact]
    public async Task An_unfinished_count_is_skipped_rather_than_compared()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSnapshot(new DateTime(2026, 8, 5), StockSnapshotStatus.Failed);
        AddSnapshot(new DateTime(2026, 8, 6), ("CHE011", 100m));
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var morning = Assert.Single(report.Mornings);

        Assert.Equal(new DateTime(2026, 8, 4), morning.FromSnapshot);
        Assert.Equal(new DateTime(2026, 8, 6), morning.ToSnapshot);
        Assert.True(morning.TiesToSap);
        Assert.Equal(1, report.Quality.IncompleteSnapshots);
    }

    // --- Sales against SAP invoices ---

    /// <summary>
    /// Recorded by the van, never invoiced. SAP overstates the van's stock until it is, and the
    /// report names the items.
    /// </summary>
    [Fact]
    public async Task Sales_with_no_sap_invoice_are_reported_as_not_in_sap()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m), ("NRI049", 50m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m), ("NRI049", 10m));
        _sap.Invoice(779001, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4, 18, 0, 0), ("CHE011", 60m));
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var day = Assert.Single(report.SalesDays);

        Assert.Equal("NotInSap", day.Status);
        Assert.Equal(1, day.ItemsNotInSap);
        var item = Assert.Single(day.Differences);
        Assert.Equal("NRI049", item.ItemCode);
        Assert.Equal(-10m, item.Difference);

        Assert.Equal(1, Assert.Single(report.Vans!).SalesDaysNotInSap);
    }

    /// <summary>
    /// SAP holds invoices from the van for a day the app recorded nothing — raised by hand, or a sale
    /// the app lost. Either way someone should know who raised them.
    /// </summary>
    [Fact]
    public async Task Sap_invoices_with_no_recorded_sale_are_reported_as_sap_only()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        _sap.Invoice(779001, new DateTime(2026, 8, 4), new DateTime(2026, 8, 4, 18, 0, 0), ("CHE011", 20m));
        await _context.SaveChangesAsync();

        var day = Assert.Single((await RunAsync()).SalesDays);

        Assert.Equal("SapOnly", day.Status);
        Assert.Equal(1, day.ItemsOnlyInSap);
    }

    /// <summary>
    /// The van's own business partner is what SAP invoices are read by, and it comes from the rep's
    /// profile — SAP files VAN001's invoices under VAN010, so a warehouse code would find nothing.
    /// </summary>
    [Fact]
    public async Task Invoices_are_read_by_the_reps_business_partner()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        await _context.SaveChangesAsync();

        await RunAsync();

        Assert.Equal([Van], _sap.Accounts![Van]);
    }

    /// <summary>
    /// When SAP cannot be read nothing is claimed either way: no morning ties, no day is on time, and
    /// the reason leads the caveats.
    /// </summary>
    [Fact]
    public async Task Without_sap_nothing_is_claimed_either_way()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 40m));
        _sap.Problem = "SAP documents could not be read: timeout.";
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var morning = Assert.Single(report.Mornings);
        Assert.False(morning.SapChecked);
        Assert.False(morning.TiesToSap);
        Assert.Empty(morning.Unexplained);

        Assert.Equal("Unchecked", Assert.Single(report.SalesDays).Status);
        Assert.False(report.Summary.SapChecked);
        Assert.StartsWith("SAP documents could not be read", report.Quality.Caveats.First());
    }

    /// <summary>
    /// One row per van, in counts of items. Item-mornings are what "items selling" is taken from,
    /// and a van's idle items are only those it carried itself for the threshold without a sale.
    /// </summary>
    [Fact]
    public async Task Each_van_gets_its_own_counts_of_items_and_selling_days()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m), ("PIC003", 20m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 40m), ("PIC003", 20m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        await _context.SaveChangesAsync();

        var van = Assert.Single((await RunAsync(deadStockDays: 2)).Vans!);

        Assert.Equal(Van, van.VanWarehouseCode);
        Assert.Equal(2, van.DaysCounted);
        Assert.Equal(1, van.DaysWithSales);
        Assert.Equal(2, van.ItemCount);
        Assert.Equal(4, van.ItemDays);
        Assert.Equal(1, van.SoldItemDays);
        // PIC003 rode both mornings and never sold; CHE011 sold on the 4th.
        Assert.Equal(1, van.DeadItemCount);
    }

    /// <summary>A van with no count in the period still gets a row, so the page can say so.</summary>
    [Fact]
    public async Task A_van_with_no_count_still_gets_a_row()
    {
        await _context.SaveChangesAsync();

        var van = Assert.Single((await RunAsync()).Vans!);

        Assert.Equal(Van, van.VanWarehouseCode);
        Assert.Equal(0, van.DaysCounted);
        Assert.Equal(0, van.ItemDays);
    }

    // --- C3 / C4: sell-through and dead stock ---

    /// <summary>
    /// The dead-stock measure. An item carried every day and never sold is capital and shelf space
    /// that could be holding something that moves.
    /// </summary>
    [Fact]
    public async Task An_item_carried_and_never_sold_is_reported_as_dead()
    {
        foreach (var day in new[] { 4, 5, 6 })
        {
            AddSnapshot(new DateTime(2026, 8, day), ("CHE011", 100m), ("PIC003", 20m));
        }

        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 30m));
        AddSale("S2", new DateTime(2026, 8, 5), ("CHE011", 30m));
        await _context.SaveChangesAsync();

        var report = await RunAsync(deadStockDays: 3);

        var dead = report.Items.Single(item => item.ItemCode == "PIC003");
        Assert.True(dead.IsDead);
        Assert.Equal(3, dead.DaysOnVanWithoutSelling);
        Assert.Equal(0, dead.DaysSold);
        Assert.Null(dead.LastSoldOn);

        var mover = report.Items.Single(item => item.ItemCode == "CHE011");
        Assert.False(mover.IsDead);
        Assert.Equal(2, mover.DaysSold);
        Assert.Equal(new DateTime(2026, 8, 5), mover.LastSoldOn);

        Assert.Equal(1, report.Summary.DeadItemCount);
    }

    /// <summary>A van loaded with nothing has no sell-through — not a zero one.</summary>
    [Fact]
    public async Task A_van_with_no_load_has_no_sell_through()
    {
        AddSnapshot(new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        Assert.Null(Assert.Single((await RunAsync()).Days).SellThroughRate);
    }

    // --- C5: expiry ---

    /// <summary>
    /// Expiry is a question about what is on the van now, so it reads the newest snapshot per van —
    /// not every snapshot in the period, which would report the same batch once a day.
    /// </summary>
    [Fact]
    public async Task Expiry_is_read_from_the_newest_snapshot_only()
    {
        var soon = DateTime.UtcNow.AddHours(2).Date.AddDays(10);

        AddSnapshotRows(new DateTime(2026, 8, 4), ("CHE011", 100m, "BATCH-A", soon));
        AddSnapshotRows(new DateTime(2026, 8, 5), ("CHE011", 90m, "BATCH-A", soon));
        await _context.SaveChangesAsync();

        var expiring = Assert.Single((await RunAsync()).Expiring);

        Assert.Equal("BATCH-A", expiring.BatchNumber);
        Assert.Equal(new DateTime(2026, 8, 5), expiring.SnapshotDate);
        Assert.Equal(90m, expiring.Quantity);
        Assert.False(expiring.HasExpired);
    }

    /// <summary>A batch already past its date leads the list.</summary>
    [Fact]
    public async Task An_expired_batch_still_on_the_van_is_reported_first()
    {
        var today = DateTime.UtcNow.AddHours(2).Date;

        AddSnapshotRows(new DateTime(2026, 8, 5),
            ("CHE011", 10m, "GONE-OFF", today.AddDays(-3)),
            ("NRI049", 20m, "FINE", today.AddDays(20)));
        await _context.SaveChangesAsync();

        var expiring = (await RunAsync()).Expiring;

        Assert.Equal(2, expiring.Count);
        Assert.True(expiring[0].HasExpired);
        Assert.Equal("GONE-OFF", expiring[0].BatchNumber);
    }

    // --- Snapshot freshness ---

    /// <summary>
    /// The failure this report is most exposed to. If the snapshot job stops, every figure describes
    /// an older morning and nothing else on the page would say so.
    /// </summary>
    [Fact]
    public async Task A_stale_snapshot_is_reported_with_its_age()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(new DateTime(2026, 8, 4), report.Summary.LatestSnapshotDate);
        Assert.True(report.Summary.IsStale);
        Assert.True(report.Summary.SnapshotAgeDays > 0);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("day(s) old"));
    }

    /// <summary>
    /// A sale from a warehouse with no snapshot sold stock this report never saw arrive. Counted
    /// rather than silently producing a van that appears to have sold from nothing.
    /// </summary>
    [Fact]
    public async Task Sales_from_a_van_with_no_snapshot_are_counted()
    {
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 30m));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Quality.VansWithNoSnapshot);
        Assert.Equal(1, report.Quality.SalesForWarehousesWithNoSnapshot);
    }

    [Fact]
    public async Task A_backwards_period_is_refused()
    {
        var handler = NewHandler();

        var result = await handler.Handle(
            new GetVanStockReportQuery(To, From),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidRange", result.FirstError.Code);
    }

    // --- Helpers ---

    private GetVanStockReportHandler NewHandler() =>
        new(_context, _sap, Options.Create(new DailyStockSettings { StockFetchTimeCAT = "07:00" }));

    private async Task<VanStockReportResult> RunAsync(int deadStockDays = 14)
    {
        var handler = NewHandler();

        var result = await handler.Handle(
            new GetVanStockReportQuery(From, To, null, deadStockDays),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private void AddSnapshot(DateTime date, params (string Item, decimal Quantity)[] items) =>
        AddSnapshotRows(date, items.Select(i => (i.Item, i.Quantity, (string?)null, (DateTime?)null)).ToArray());

    private void AddSnapshotBatches(DateTime date, params (string Item, decimal Quantity, string? Batch)[] items) =>
        AddSnapshotRows(date, items.Select(i => (i.Item, i.Quantity, i.Batch, (DateTime?)null)).ToArray());

    /// <summary>
    /// Writes a snapshot and its rows.
    /// </summary>
    /// <remarks>
    /// The header goes through EF; the item rows go in by hand, and they have to. Their
    /// <c>Version</c> column is <c>[Timestamp]</c> on a <c>uint</c>, which maps to Postgres's
    /// <c>xmin</c> — a value the database supplies — so EF omits it from the INSERT and then reads
    /// it back. SQLite has no <c>xmin</c>, so the write fails and then the read-back fails. Writing
    /// the rows directly sidesteps both without touching the production mapping, which is correct
    /// for the database this actually runs on.
    /// </remarks>
    private void AddSnapshotRows(
        DateTime date,
        params (string Item, decimal Quantity, string? Batch, DateTime? Expiry)[] items)
    {
        var header = new DailyStockSnapshotEntity
        {
            SnapshotDate = date,
            WarehouseCode = Van,
            Status = StockSnapshotStatus.Complete,
            ItemCount = items.Length
        };

        _context.DailyStockSnapshots.Add(header);
        _context.SaveChanges();

        foreach (var item in items)
        {
            // Two statements rather than a DBNull parameter, which EF has no store mapping for.
            var expiry = item.Expiry.HasValue ? "{7}" : "NULL";

            var sql =
                "INSERT INTO \"DailyStockSnapshotItems\" " +
                "(\"SnapshotId\", \"ItemCode\", \"ItemDescription\", \"WarehouseCode\", \"BatchNumber\", " +
                " \"OriginalQuantity\", \"AvailableQuantity\", \"ExpiryDate\", \"Version\") " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, " + expiry + ", 1)";

            var parameters = new List<object>
            {
                header.Id,
                item.Item,
                $"Item {item.Item}",
                Van,
                item.Batch ?? $"B-{item.Item}",
                // The morning load, taken fresh from SAP. The only quantity that means anything on a
                // van, since no van path decrements the running one.
                item.Quantity,
                item.Quantity
            };

            if (item.Expiry.HasValue)
            {
                parameters.Add(item.Expiry.Value);
            }

            _context.Database.ExecuteSqlRaw(sql, parameters.ToArray());
        }
    }

    private void AddSnapshot(DateTime date, StockSnapshotStatus status)
    {
        _context.DailyStockSnapshots.Add(new DailyStockSnapshotEntity
        {
            SnapshotDate = date,
            WarehouseCode = Van,
            Status = status
        });
        _context.SaveChanges();
    }

    private void AddAdjustment(
        DateTime date,
        string itemCode,
        decimal quantity,
        int? docEntry = null,
        DateTime? detectedAtUtc = null) =>
        _context.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
        {
            SnapshotDate = date,
            ItemCode = itemCode,
            WarehouseCode = Van,
            AdjustmentQuantity = quantity,
            Direction = quantity >= 0 ? "In" : "Out",
            TransferDocEntry = docEntry,
            DetectedAt = detectedAtUtc ?? date.AddHours(10)
        });

    private void AddSale(string reference, DateTime docDate, params (string Item, decimal Quantity)[] items) =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = Van,
            CardName = "Van 010",
            RouteCustomerCode = "TUCK01",
            RouteCustomerName = "Tuck Shop",
            DocDate = docDate,
            TotalAmount = items.Sum(i => i.Quantity),
            VatAmount = 0m,
            Currency = "USD",
            WarehouseCode = Van,
            PaymentMethod = "Cash",
            AmountPaid = items.Sum(i => i.Quantity),
            CreatedBy = Rep.ToString(),
            Lines = items
                .Select((item, index) => new DesktopSaleLineEntity
                {
                    LineNum = index,
                    ItemCode = item.Item,
                    ItemDescription = $"Item {item.Item}",
                    Quantity = item.Quantity,
                    UnitPrice = 1m,
                    LineTotal = item.Quantity,
                    WarehouseCode = Van
                })
                .ToList()
        });

    /// <summary>
    /// The SAP half, as SAP would answer it: each document with its printed date and the moment SAP
    /// created it. Transfers carry a creation date only, as they do in SAP.
    /// </summary>
    private sealed class FakeSapDocuments : IVanStockSapDocuments
    {
        private readonly List<VanStockSapMovement> _movements = [];

        public string? Problem { get; set; }

        public IReadOnlyDictionary<string, IReadOnlyCollection<string>>? Accounts { get; private set; }

        public Task<VanStockSapRead> LoadAsync(
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> accountsByVan,
            DateTime createdFrom,
            DateTime createdTo,
            CancellationToken cancellationToken)
        {
            Accounts = accountsByVan;

            return Task.FromResult(Problem is null
                ? new VanStockSapRead(true, null, _movements.ToList())
                : VanStockSapRead.Unavailable(Problem));
        }

        public void Invoice(int docNum, DateTime docDate, DateTime createdAt, params (string Item, decimal Quantity)[] lines) =>
            _movements.AddRange(lines.Select(line => Movement(
                VanStockDocumentKind.Invoice, docNum, docDate, createdAt, true, line.Item, -line.Quantity)));

        public void CreditNote(int docNum, DateTime docDate, DateTime createdAt, string item, decimal quantity) =>
            _movements.Add(Movement(VanStockDocumentKind.CreditNote, docNum, docDate, createdAt, true, item, quantity));

        public void TransferIn(int docNum, DateTime docDate, DateTime createdOn, string item, decimal quantity) =>
            _movements.Add(Movement(VanStockDocumentKind.TransferIn, docNum, docDate, createdOn.Date.AddHours(12), false, item, quantity));

        public void TransferOut(int docNum, DateTime docDate, DateTime createdOn, string item, decimal quantity) =>
            _movements.Add(Movement(VanStockDocumentKind.TransferOut, docNum, docDate, createdOn.Date.AddHours(12), false, item, -quantity));

        private static VanStockSapMovement Movement(
            VanStockDocumentKind kind, int docNum, DateTime docDate, DateTime createdAt, bool timeKnown,
            string item, decimal quantity) =>
            new(Van, kind, docNum, docNum, docDate, createdAt, timeKnown, null, item, $"Item {item}", quantity);
    }
}
