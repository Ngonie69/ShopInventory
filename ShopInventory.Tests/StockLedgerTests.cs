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
/// The one running answer to "how much of this is left to promise today".
/// </summary>
/// <remarks>
/// Two ledgers used to gate the documents this system posts and they could not see each other. Shop
/// tills validated against the morning snapshot; web invoices read live SAP. So the same units could
/// be sold twice — once from each side — and both documents were individually valid against the
/// ledger each consulted. The first test here is that case, and it is the one the plan for this
/// phase named: a till sale and a web invoice competing for the same last unit, in either order.
/// </remarks>
public sealed class StockLedgerTests
{
    private const string Warehouse = "KEFSHOP";
    private const string Untracked = "FACTORY";
    private const string Item = "CHE011";

    // ---------------------------------------------------------------
    // The double-spend
    // ---------------------------------------------------------------

    [Fact]
    public async Task Two_documents_cannot_both_take_the_last_unit()
    {
        await using var context = await LedgerWith(units: 1);
        var ledger = Ledger(context);

        var till = await ledger.TryCommitAsync([Line(1)], "till sale");
        var web = await ledger.TryCommitAsync([Line(1)], "web invoice");

        Assert.True(till.Committed);
        Assert.False(web.Committed);
        Assert.Contains(web.Shortfalls, s => s.Contains("1 requested, 0 left"));
    }

    [Fact]
    public async Task The_same_holds_with_the_web_invoice_first()
    {
        await using var context = await LedgerWith(units: 1);
        var ledger = Ledger(context);

        var web = await ledger.TryCommitAsync([Line(1)], "web invoice");
        var till = await ledger.TryCommitAsync([Line(1)], "till sale");

        Assert.True(web.Committed);
        Assert.False(till.Committed);
    }

    [Fact]
    public async Task A_document_takes_every_line_or_none_of_them()
    {
        await using var context = await LedgerWith(units: 10);
        await AddRow(context, "BON001", Warehouse, units: 1);
        var ledger = Ledger(context);

        // Plenty of the first item, not enough of the second.
        var outcome = await ledger.TryCommitAsync(
            [Line(5), new StockLedgerLine("BON001", Warehouse, 4)],
            "mixed basket");

        Assert.False(outcome.Committed);

        // The first line must not have been taken. A partial commit would leave five units
        // promised to a document that was refused, and nothing would ever give them back.
        var reading = await ledger.ReadAsync(Item, Warehouse);
        Assert.Equal(10m, reading.Available);
    }

    [Fact]
    public async Task Two_lines_of_one_item_are_one_demand_on_the_shelf()
    {
        await using var context = await LedgerWith(units: 10);
        var ledger = Ledger(context);

        // Six and six against ten. Checked line by line each would pass.
        var outcome = await ledger.TryCommitAsync([Line(6), Line(6)], "split lines");

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Shortfalls, s => s.Contains("12 requested"));
    }

    // ---------------------------------------------------------------
    // Coverage: an absent snapshot is not an empty warehouse
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_warehouse_with_no_snapshot_is_reported_not_refused()
    {
        await using var context = await LedgerWith(units: 5);
        var ledger = Ledger(context);

        var outcome = await ledger.TryCommitAsync(
            [new StockLedgerLine(Item, Untracked, 999)], "web invoice elsewhere");

        // Most warehouses have no till selling from them, so there is no second consumer to collide
        // with and SAP is authority enough. Reading the absent snapshot as zero would refuse every
        // invoice for every one of them.
        Assert.True(outcome.Committed);
        Assert.Equal([Untracked], outcome.UntrackedWarehouses);
    }

    [Fact]
    public async Task Coverage_says_so_plainly_on_a_read()
    {
        await using var context = await LedgerWith(units: 5);
        var ledger = Ledger(context);

        Assert.Equal(StockLedgerCoverage.Tracked, (await ledger.ReadAsync(Item, Warehouse)).Coverage);
        Assert.Equal(StockLedgerCoverage.NotTracked, (await ledger.ReadAsync(Item, Untracked)).Coverage);
    }

    [Fact]
    public async Task A_snapshot_that_never_finished_does_not_count_as_coverage()
    {
        await using var context = await LedgerWith(units: 5, status: StockSnapshotStatus.Failed);
        var ledger = Ledger(context);

        // Half a warehouse read as a whole one would be worse than no ledger: it would refuse the
        // items the failed fetch never wrote and allow the rest against figures nobody trusts.
        Assert.Equal(StockLedgerCoverage.NotTracked, (await ledger.ReadAsync(Item, Warehouse)).Coverage);
    }

    // ---------------------------------------------------------------
    // Taking and giving back
    // ---------------------------------------------------------------

    [Fact]
    public async Task Units_come_off_the_batch_that_expires_first()
    {
        await using var context = EmptyContext();
        var snapshot = await AddSnapshot(context);
        await AddRow(context, Item, Warehouse, units: 4, snapshot: snapshot, expiry: new DateTime(2026, 12, 1), batch: "LATE");
        await AddRow(context, Item, Warehouse, units: 4, snapshot: snapshot, expiry: new DateTime(2026, 9, 1), batch: "SOON");

        var ledger = Ledger(context);
        await ledger.TryCommitAsync([Line(5)], "fefo");

        var rows = await context.DailyStockSnapshotItems.ToListAsync();
        Assert.Equal(0m, rows.Single(r => r.BatchNumber == "SOON").AvailableQuantity);
        Assert.Equal(3m, rows.Single(r => r.BatchNumber == "LATE").AvailableQuantity);
    }

    [Fact]
    public async Task A_released_claim_can_be_sold_again()
    {
        await using var context = await LedgerWith(units: 3);
        var ledger = Ledger(context);

        Assert.True((await ledger.TryCommitAsync([Line(3)], "invoice")).Committed);
        Assert.False((await ledger.TryCommitAsync([Line(1)], "next")).Committed);

        await ledger.ReleaseAsync([Line(3)], "invoice was refused by SAP");

        Assert.True((await ledger.TryCommitAsync([Line(3)], "retry")).Committed);
    }

    [Fact]
    public async Task Releasing_into_an_untracked_warehouse_is_harmless()
    {
        await using var context = await LedgerWith(units: 3);
        var ledger = Ledger(context);

        await ledger.ReleaseAsync([new StockLedgerLine(Item, Untracked, 5)], "nothing to return to");

        Assert.Equal(3m, (await ledger.ReadAsync(Item, Warehouse)).Available);
    }

    [Fact]
    public async Task Nothing_is_taken_for_a_zero_or_negative_line()
    {
        await using var context = await LedgerWith(units: 3);
        var ledger = Ledger(context);

        var outcome = await ledger.TryCommitAsync(
            [new StockLedgerLine(Item, Warehouse, 0), new StockLedgerLine(Item, Warehouse, -2)],
            "empty basket");

        Assert.True(outcome.Committed);
        Assert.Equal(3m, (await ledger.ReadAsync(Item, Warehouse)).Available);
    }

    // ---------------------------------------------------------------
    // Reservations hold, without any bookkeeping to lose
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_live_reservation_holds_stock_against_the_till()
    {
        await using var context = await LedgerWith(units: 10);
        await AddReservation(context, quantity: 8, status: ReservationStatus.Pending);
        var ledger = Ledger(context);

        // This is the one-way hold closed. The SAP-side path already netted reservations off; the
        // till read the raw snapshot and would sell stock a rep had reserved minutes earlier for a
        // customer standing in the shop.
        Assert.Equal(2m, (await ledger.ReadAsync(Item, Warehouse)).Available);

        var outcome = await ledger.TryCommitAsync([Line(5)], "till sale");
        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Shortfalls, s => s.Contains("8 of it reserved"));
    }

    [Fact]
    public async Task A_cancelled_or_expired_reservation_stops_holding_anything()
    {
        await using var context = await LedgerWith(units: 10);
        await AddReservation(context, quantity: 8, status: ReservationStatus.Cancelled);
        await AddReservation(context, quantity: 5, status: ReservationStatus.Pending, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        // No bookkeeping to get wrong: the reservation's own status is the lifecycle, so nothing has
        // to remember to give units back and nothing can lose them.
        Assert.Equal(10m, (await Ledger(context).ReadAsync(Item, Warehouse)).Available);
    }

    [Fact]
    public async Task A_confirmed_reservation_stops_holding_and_its_units_are_taken_instead()
    {
        await using var context = await LedgerWith(units: 10);
        var reservation = await AddReservation(context, quantity: 8, status: ReservationStatus.Pending);
        var ledger = Ledger(context);

        Assert.Equal(2m, (await ledger.ReadAsync(Item, Warehouse)).Available);

        // What ConfirmReservationAsync does: the status change drops the hold, and the invoice it
        // posted is recorded as settled. Counted once across the two steps, not twice and not zero.
        reservation.Status = ReservationStatus.Confirmed;
        await context.SaveChangesAsync();
        await ledger.TakeSettledAsync([Line(8)], "reservation confirmed as invoice 5001");

        Assert.Equal(2m, (await ledger.ReadAsync(Item, Warehouse)).Available);
    }

    // ---------------------------------------------------------------
    // Settled documents, which cannot be refused
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_settled_document_is_recorded_even_when_the_ledger_is_short()
    {
        await using var context = await LedgerWith(units: 2);
        var ledger = Ledger(context);

        // The invoice exists in SAP. Refusing to record it would leave the ledger claiming stock
        // that has physically gone, which is the direction that oversells.
        await ledger.TakeSettledAsync([Line(5)], "already in SAP");

        Assert.Equal(0m, (await ledger.ReadAsync(Item, Warehouse)).Available);
    }

    [Fact]
    public async Task A_returned_unit_can_be_sold_again()
    {
        await using var context = await LedgerWith(units: 5);
        var ledger = Ledger(context);

        Assert.True((await ledger.TryCommitAsync([Line(5)], "invoice")).Committed);
        Assert.False((await ledger.TryCommitAsync([Line(2)], "next sale")).Committed);

        // What a credit note does once SAP has taken it. Without this the ledger only ever falls,
        // and by evening a shop that took a delivery back is refused sales for stock standing in
        // front of the cashier.
        await ledger.ReleaseAsync([Line(2)], "credit note 8801");

        Assert.True((await ledger.TryCommitAsync([Line(2)], "next sale")).Committed);
    }

    // ---------------------------------------------------------------
    // Who commits, and who must not
    // ---------------------------------------------------------------

    [Fact]
    public void The_paths_that_take_an_obligation_use_the_ledger()
    {
        // A source check, like the invoice-handler one in Phase 3 and for the same reason: nothing
        // in the suite stands these handlers' dependencies up yet, and the defect the ledger fixes
        // is structural — a path that consults nothing.
        Assert.Contains("stockLedger.TryCommitAsync", Source(
            "Features", "DesktopIntegration", "Commands", "CreateDesktopSale", "CreateDesktopSaleHandler.cs"));

        Assert.Contains("stockLedger.TryCommitAsync", Source(
            "Features", "Invoices", "Commands", "CreateInvoice", "CreateInvoiceHandler.cs"));

        // A credit note returns units; a confirmed reservation records the ones its invoice took.
        //
        // Asserted on the call, not just on the method that makes it. A helper that nobody invokes
        // still contains the words — the first version of this test passed happily with both call
        // sites deleted, which is exactly the regression it is here to catch.
        var creditNotes = Source("Services", "CreditNoteService.cs");
        Assert.Contains("_stockLedger.ReleaseAsync", creditNotes);
        Assert.Contains("await ReturnRestockedUnitsToLedgerAsync(request,", creditNotes);

        var reservations = Source("Services", "StockReservationService.cs");
        Assert.Contains("_stockLedger.TakeSettledAsync", reservations);
        Assert.Contains("await CommitConfirmedReservationToLedgerAsync(reservation,", reservations);
    }

    [Fact]
    public void The_paths_that_only_post_an_existing_obligation_do_not_commit_again()
    {
        // A till sale commits when it is captured, because that is when the goods leave. The job
        // that carries it to SAP a minute later, and the 18:00 consolidation that invoices a day of
        // them, are both moving an obligation that has already been accounted for.
        //
        // Double-committing here would be quieter than the bug the ledger replaces: no document is
        // wrong, stock simply goes missing from the ledger and the tills stop selling things they
        // have.
        Assert.DoesNotContain("TryCommitAsync", Source("Services", "DesktopSalePostingService.cs"));
        Assert.DoesNotContain("TryCommitAsync", Source(
            "Features", "DesktopIntegration", "Commands", "ConsolidateDailySales", "ConsolidateDailySalesHandler.cs"));
    }

    // ---------------------------------------------------------------

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), "ShopInventory", .. parts]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static async Task<StockReservationEntity> AddReservation(
        ApplicationDbContext context,
        decimal quantity,
        string status,
        DateTime? expiresAt = null)
    {
        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = Guid.NewGuid().ToString("N"),
            SourceSystem = "TEST",
            CardCode = "C-1",
            Status = status,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
            Lines =
            [
                new StockReservationLineEntity
                {
                    ItemCode = Item,
                    WarehouseCode = Warehouse,
                    ReservedQuantity = quantity
                }
            ]
        };

        context.StockReservations.Add(reservation);
        await context.SaveChangesAsync();
        return reservation;
    }

    private static StockLedgerLine Line(decimal quantity) => new(Item, Warehouse, quantity);

    private static StockLedger Ledger(ApplicationDbContext context) =>
        new(context, Options.Create(new DailyStockSettings()), NullLogger<StockLedger>.Instance);

    private static async Task<ApplicationDbContext> LedgerWith(
        decimal units,
        StockSnapshotStatus status = StockSnapshotStatus.Complete)
    {
        var context = EmptyContext();
        var snapshot = await AddSnapshot(context, status);
        await AddRow(context, Item, Warehouse, units, snapshot);
        return context;
    }

    private static async Task<DailyStockSnapshotEntity> AddSnapshot(
        ApplicationDbContext context,
        StockSnapshotStatus status = StockSnapshotStatus.Complete)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            // Stamped the way the morning job stamps it. If the job and the ledger ever disagree
            // about which day is in force, every test in this file stops finding its rows.
            SnapshotDate = StockLedgerDay.Today(new DailyStockSettings().StockFetchTimeCAT),
            WarehouseCode = Warehouse,
            Status = status
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
            OriginalQuantity = units,
            AvailableQuantity = units,
            ExpiryDate = expiry,
            // See SnapshotSqliteContext for why this is set by hand here, and for why the
            // ledger's retry-on-conflict path is not reachable from this suite.
            Version = 1
        });

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
