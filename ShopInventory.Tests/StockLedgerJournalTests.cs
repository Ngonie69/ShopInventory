using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What moved the ledger, and the guarantee that a document moves it once.
/// </summary>
/// <remarks>
/// The running figure on a snapshot row said what was left; nothing said what had taken it. The
/// movements existed only as log lines, which cannot be queried, counted, or used to decide
/// anything — so a document that committed twice took the units twice and there was no reading
/// afterwards that could show it had. These tests are about the second half of the fix: the journal
/// is also the idempotency key, and that is the part that prevents a wrong figure rather than
/// merely explaining one.
/// </remarks>
public sealed class StockLedgerJournalTests
{
    private const string Warehouse = "KEFSHOP";
    private const string Item = "CHE011";

    // ---------------------------------------------------------------
    // A document moves the ledger once
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_retried_commit_under_the_same_key_takes_the_units_once()
    {
        await using var context = await LedgerWith(units: 10);
        var ledger = Ledger(context);

        // The same command arriving twice — a desktop retry, a re-queued job. Before the journal
        // this took four units, and nothing afterwards could tell that it had.
        var first = await ledger.TryCommitAsync([Line(2)], "till sale", "desktop-sale:abc");
        var second = await ledger.TryCommitAsync([Line(2)], "till sale", "desktop-sale:abc");

        Assert.True(first.Committed);
        Assert.True(second.Committed);

        var reading = await ledger.ReadAsync(Item, Warehouse);
        Assert.Equal(8m, reading.Available);
        Assert.Single(await context.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task A_retried_release_under_the_same_key_returns_the_units_once()
    {
        await using var context = await LedgerWith(units: 10);
        var ledger = Ledger(context);

        await ledger.TryCommitAsync([Line(4)], "invoice", "invoice:SO-1");
        await ledger.ReleaseAsync([Line(4)], "invoice not posted (validation error)", "invoice:SO-1");
        await ledger.ReleaseAsync([Line(4)], "invoice not posted (SAP refused)", "invoice:SO-1");

        // Returning twice is the direction that oversells: the ledger would promise 14 off a shelf
        // holding 10.
        var reading = await ledger.ReadAsync(Item, Warehouse);
        Assert.Equal(10m, reading.Available);
    }

    [Fact]
    public async Task A_retried_settlement_under_the_same_key_takes_the_units_once()
    {
        await using var context = await LedgerWith(units: 10);
        var ledger = Ledger(context);

        await ledger.TakeSettledAsync([Line(3)], "van sale", "van-sale:V-77");
        await ledger.TakeSettledAsync([Line(3)], "van sale", "van-sale:V-77");

        var reading = await ledger.ReadAsync(Item, Warehouse);
        Assert.Equal(7m, reading.Available);
    }

    [Fact]
    public async Task Documents_with_no_key_are_never_mistaken_for_each_other()
    {
        await using var context = await LedgerWith(units: 10);
        var ledger = Ledger(context);

        // Two genuinely different invoices, neither carrying an identity. Deduplicating these would
        // be the worse failure: the second document would take nothing and nobody would be told.
        await ledger.TryCommitAsync([Line(2)], "invoice", documentKey: null);
        await ledger.TryCommitAsync([Line(2)], "invoice", documentKey: null);

        var reading = await ledger.ReadAsync(Item, Warehouse);
        Assert.Equal(6m, reading.Available);
    }

    [Fact]
    public async Task A_different_key_of_the_same_kind_is_a_different_document()
    {
        await using var context = await LedgerWith(units: 10);
        var ledger = Ledger(context);

        await ledger.TryCommitAsync([Line(2)], "till sale", "desktop-sale:abc");
        await ledger.TryCommitAsync([Line(2)], "till sale", "desktop-sale:def");

        var reading = await ledger.ReadAsync(Item, Warehouse);
        Assert.Equal(6m, reading.Available);
    }

    [Fact]
    public async Task A_commit_and_a_release_of_one_document_are_separate_movements()
    {
        await using var context = await LedgerWith(units: 10);
        var ledger = Ledger(context);

        // The same key on both, because it is the same document. Only the kind tells them apart,
        // and if it did not the release would be swallowed as a repeat of the commit.
        await ledger.TryCommitAsync([Line(4)], "invoice", "invoice:SO-1");
        await ledger.ReleaseAsync([Line(4)], "invoice not posted", "invoice:SO-1");

        var reading = await ledger.ReadAsync(Item, Warehouse);
        Assert.Equal(10m, reading.Available);

        var kinds = await context.StockMovements.OrderBy(m => m.Id).Select(m => m.Kind).ToListAsync();
        Assert.Equal([StockMovementKinds.Commit, StockMovementKinds.Release], kinds);
    }

    [Fact]
    public async Task A_refused_commit_journals_nothing_and_does_not_burn_its_key()
    {
        await using var context = await LedgerWith(units: 1);
        var ledger = Ledger(context);

        var outcome = await ledger.TryCommitAsync([Line(5)], "till sale", "desktop-sale:abc");

        Assert.False(outcome.Committed);
        Assert.Empty(await context.StockMovements.ToListAsync());

        // The document never committed, so a later attempt that can be covered must still go
        // through rather than be waved past as a repeat.
        await AddRow(context, Item, Warehouse, units: 10);
        var retry = await ledger.TryCommitAsync([Line(5)], "till sale", "desktop-sale:abc");
        Assert.True(retry.Committed);
    }

    // ---------------------------------------------------------------
    // What the journal records
    // ---------------------------------------------------------------

    [Fact]
    public async Task The_journal_accounts_for_every_unit_the_ledger_moved()
    {
        await using var context = await LedgerWith(units: 10);
        var ledger = Ledger(context);

        await ledger.TryCommitAsync([Line(3)], "till sale", "desktop-sale:abc");
        await ledger.TakeSettledAsync([Line(2)], "van sale", "van-sale:V-1");
        await ledger.ReleaseAsync([Line(1)], "credit note", "credit-note:99");

        // The invariant the table exists for: the opening figure plus everything journalled is the
        // figure the guard reads. A writer that moves the cell without a journal row breaks this,
        // which is how the next one will be caught.
        var row = await context.DailyStockSnapshotItems.SingleAsync(r => r.ItemCode == Item);
        var journalled = await context.StockMovements
            .Where(m => m.ItemCode == Item && m.WarehouseCode == Warehouse)
            .SumAsync(m => m.Quantity);

        Assert.Equal(6m, row.AvailableQuantity);
        Assert.Equal(row.AvailableQuantity, row.OriginalQuantity + journalled);
    }

    [Fact]
    public async Task A_claim_spread_over_several_rows_is_one_movement()
    {
        await using var context = EmptyContext();
        var snapshot = await AddSnapshot(context);
        await AddRow(context, Item, Warehouse, units: 4, snapshot, expiry: new DateTime(2026, 10, 1), batch: "B1");
        await AddRow(context, Item, Warehouse, units: 6, snapshot, expiry: new DateTime(2026, 11, 1), batch: "B2");

        // Nine units across two batches, soonest to expire first: all of B1, five of B2. One
        // document, one claim, one movement — journalling the two draws separately would have a
        // single claim collide with itself on the index that makes the document idempotent.
        await Ledger(context).TryCommitAsync([Line(9)], "till sale", "desktop-sale:abc");

        var movement = await context.StockMovements.SingleAsync();
        Assert.Equal(-9m, movement.Quantity);
        Assert.Equal(1m, movement.BalanceAfter);

        // The per-batch position is still on the snapshot rows, which is where it belongs.
        var rows = await context.DailyStockSnapshotItems.OrderBy(r => r.BatchNumber).ToListAsync();
        Assert.Equal(0m, rows[0].AvailableQuantity);
        Assert.Equal(1m, rows[1].AvailableQuantity);
    }

    [Fact]
    public async Task A_settlement_journals_what_the_rows_actually_gave()
    {
        await using var context = await LedgerWith(units: 3);

        // A settled document cannot be refused, so it takes what is there and reports the rest as a
        // shortfall. The journal has to record the three units that moved, not the eight that were
        // claimed, or the balance and the journal stop agreeing.
        var shortfalls = await Ledger(context).TakeSettledAsync([Line(8)], "van sale", "van-sale:V-2");

        Assert.Single(shortfalls);

        var row = await context.DailyStockSnapshotItems.SingleAsync();
        var movement = await context.StockMovements.SingleAsync();
        Assert.Equal(-3m, movement.Quantity);
        Assert.Equal(0m, movement.BalanceAfter);
        Assert.Equal(row.AvailableQuantity, row.OriginalQuantity + movement.Quantity);
    }

    // ---------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------

    private static StockLedgerLine Line(decimal quantity) => new(Item, Warehouse, quantity);

    private static StockLedger Ledger(ApplicationDbContext context) =>
        new(context, Options.Create(new DailyStockSettings()), NullLogger<StockLedger>.Instance);

    private static async Task<ApplicationDbContext> LedgerWith(decimal units)
    {
        var context = EmptyContext();
        var snapshot = await AddSnapshot(context);
        await AddRow(context, Item, Warehouse, units, snapshot);
        return context;
    }

    private static async Task<DailyStockSnapshotEntity> AddSnapshot(ApplicationDbContext context)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = StockLedgerDay.Today(new DailyStockSettings().StockFetchTimeCAT),
            WarehouseCode = Warehouse,
            Status = StockSnapshotStatus.Complete
        };

        context.DailyStockSnapshots.Add(snapshot);
        await context.SaveChangesAsync();
        return snapshot;
    }

    private static async Task AddRow(
        ApplicationDbContext context,
        string itemCode,
        string warehouseCode,
        decimal units,
        DailyStockSnapshotEntity? snapshot = null,
        DateTime? expiry = null,
        string? batch = null)
    {
        snapshot ??= await context.DailyStockSnapshots.FirstAsync();

        context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            BatchNumber = batch,
            ExpiryDate = expiry,
            // See SnapshotSqliteContext for why this is set by hand.
            Version = 1
        }.Opened(units));

        await context.SaveChangesAsync();
    }

    private static ApplicationDbContext EmptyContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var context = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
        context.Database.EnsureCreated();
        return context;
    }
}
