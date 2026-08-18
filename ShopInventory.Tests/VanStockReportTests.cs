using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the van stock report.
///
/// The reconciliation here is morning to morning, and the two ways it can lie are both tested below.
/// It must never reach over a missing day — two days of difference reported as one looks like a
/// single large discrepancy on the wrong date. And it must never read the snapshot's
/// <c>AvailableQuantity</c> as what is left on a van: no van sales path decrements it, so on a van
/// warehouse that column is inert and would make every van look like it sold nothing.
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

    // --- C2: variance ---

    /// <summary>
    /// The reconciliation itself. Yesterday's load, less what sold, plus what arrived, is what this
    /// morning should have found.
    /// </summary>
    [Fact]
    public async Task A_clean_pair_of_mornings_reconciles_to_no_variance()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 40m));
        await _context.SaveChangesAsync();

        var variance = Assert.Single((await RunAsync()).Variances);

        Assert.False(variance.HasGap);
        Assert.Equal(40m, variance.ExpectedQuantity);
        Assert.Equal(40m, variance.ClosingQuantity);
        Assert.Equal(0m, variance.Variance);
        Assert.Empty(variance.TopVariances);
    }

    /// <summary>Stock that went missing overnight shows as a negative variance, per item.</summary>
    [Fact]
    public async Task Stock_that_cannot_be_accounted_for_shows_as_a_shortfall()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        // Should have found 40; found 31.
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 31m));
        await _context.SaveChangesAsync();

        var variance = Assert.Single((await RunAsync()).Variances);

        Assert.Equal(-9m, variance.Variance);
        Assert.Equal(1, variance.ItemsShort);
        Assert.Equal(0, variance.ItemsOver);

        var item = Assert.Single(variance.TopVariances);
        Assert.Equal("CHE011", item.ItemCode);
        Assert.Equal(-9m, item.Variance);
    }

    /// <summary>A transfer that reached the van during the day is part of the arithmetic.</summary>
    [Fact]
    public async Task A_mid_day_transfer_is_taken_into_account()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        AddAdjustment(new DateTime(2026, 8, 4), "CHE011", 25m);
        AddSnapshot(new DateTime(2026, 8, 5), ("CHE011", 65m));
        await _context.SaveChangesAsync();

        var variance = Assert.Single((await RunAsync()).Variances);

        Assert.Equal(65m, variance.ExpectedQuantity);
        Assert.Equal(0m, variance.Variance);
    }

    /// <summary>
    /// The rule this report exists to keep. Two days of difference reported as one would read as a
    /// single large discrepancy on the wrong date, so a gap yields no variance at all.
    /// </summary>
    [Fact]
    public async Task A_missing_morning_breaks_the_chain_rather_than_being_bridged()
    {
        AddSnapshot(new DateTime(2026, 8, 4), ("CHE011", 100m));
        AddSale("S1", new DateTime(2026, 8, 4), ("CHE011", 60m));
        // Nothing on the 5th.
        AddSnapshot(new DateTime(2026, 8, 6), ("CHE011", 10m));
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var variance = Assert.Single(report.Variances);

        Assert.True(variance.HasGap);
        Assert.Equal(2, variance.GapDays);
        Assert.Null(variance.ExpectedQuantity);
        Assert.Null(variance.Variance);
        Assert.Empty(variance.TopVariances);

        Assert.Equal(1, report.Quality.VariancePairsSkippedForGaps);
        Assert.Equal(1, report.Quality.MissingSnapshotDays);
        Assert.False(report.Quality.IsClean);
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
        var handler = new GetVanStockReportHandler(_context);

        var result = await handler.Handle(
            new GetVanStockReportQuery(To, From),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidRange", result.FirstError.Code);
    }

    // --- Helpers ---

    private async Task<VanStockReportResult> RunAsync(int deadStockDays = 14)
    {
        var handler = new GetVanStockReportHandler(_context);

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

    private void AddAdjustment(DateTime date, string itemCode, decimal quantity) =>
        _context.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
        {
            SnapshotDate = date,
            ItemCode = itemCode,
            WarehouseCode = Van,
            AdjustmentQuantity = quantity,
            Direction = quantity >= 0 ? "In" : "Out",
            DetectedAt = date.AddHours(10)
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
}
