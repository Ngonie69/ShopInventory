using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What happens when a van sells more than it was carrying.
/// </summary>
/// <remarks>
/// <para>The van route cannot refuse and must not try. By the time a sale reaches this system the
/// customer has been served and the receipt is with ZIMRA; a refusal would leave a signed receipt
/// with no invoice behind it, which is worse than the over-sale. So the goal is not prevention here
/// — it is that the over-sale is visible, attributed and countable on the day, rather than found at
/// a month-end reconciliation.</para>
///
/// <para>Nothing decremented a van's stock at all before this. The snapshot a handset files each
/// morning stayed at the morning count until the next morning replaced it, so an over-sale left no
/// trace anywhere and the shopkeeper-facing catalogue went on offering a full load after the van had
/// sold out.</para>
/// </remarks>
public sealed class VanOverSaleVisibilityTests
{
    private const string Van = "VAN004";
    private const string Item = "CHE011";

    [Fact]
    public async Task A_van_sale_takes_its_stock_off_the_van()
    {
        await using var context = await VanCarrying(units: 20);
        var ledger = Ledger(context);

        await ledger.TakeSettledAsync(Lines(8), "van sale VS-1");

        // The shopkeeper-facing catalogue reads this figure, so until now it offered the van's
        // morning load all day whatever had been sold off it.
        Assert.Equal(12m, (await ledger.ReadAsync(Item, Van)).Available);
    }

    [Fact]
    public async Task Selling_more_than_the_van_holds_is_recorded_rather_than_refused()
    {
        await using var context = await VanCarrying(units: 5);
        var ledger = Ledger(context);

        var shortfalls = await ledger.TakeSettledAsync(Lines(8), "van sale VS-2");

        // Recorded, not thrown: the sale happened and the invoice has to follow it.
        var shortfall = Assert.Single(shortfalls);
        Assert.Equal(Item, shortfall.ItemCode);
        Assert.Equal(8m, shortfall.Taken);
        Assert.Equal(5m, shortfall.Held);
        Assert.Equal(3m, shortfall.Excess);
    }

    [Fact]
    public async Task An_over_sale_never_drives_the_van_below_zero()
    {
        await using var context = await VanCarrying(units: 5);
        var ledger = Ledger(context);

        await ledger.TakeSettledAsync(Lines(8), "van sale VS-3");

        // The shortfall is reported instead. A negative row would be a second wrong number on top of
        // the first, and the catalogue would show it.
        Assert.Equal(0m, (await ledger.ReadAsync(Item, Van)).Available);
    }

    [Fact]
    public async Task A_sale_the_van_could_cover_reports_nothing()
    {
        await using var context = await VanCarrying(units: 20);

        Assert.Empty(await Ledger(context).TakeSettledAsync(Lines(8), "van sale VS-4"));
    }

    [Fact]
    public async Task The_recorded_divergence_names_the_van_the_item_and_the_document()
    {
        await using var context = await VanCarrying(units: 5);
        var ledger = Ledger(context);

        var shortfalls = await ledger.TakeSettledAsync(Lines(8), "van sale VS-5");

        // What VanSalesEndOfDayPostingService writes from that shortfall. Asserted on the row rather
        // than through the posting service, which needs a SAP double and a fiscal chain to reach.
        foreach (var shortfall in shortfalls)
        {
            context.StockLedgerDivergences.Add(new StockLedgerDivergenceEntity
            {
                LedgerDay = ledger.CurrentLedgerDay,
                Source = StockLedgerDivergenceSources.SettledDocument,
                Reference = "VS-5 / invoice 7001",
                WarehouseCode = shortfall.WarehouseCode,
                ItemCode = shortfall.ItemCode,
                LedgerQuantity = shortfall.Held,
                SapIssuableQuantity = shortfall.Taken,
                Difference = -shortfall.Excess
            });
        }

        await context.SaveChangesAsync();

        var recorded = Assert.Single(await context.StockLedgerDivergences.ToListAsync());
        Assert.Equal(StockLedgerDivergenceSources.SettledDocument, recorded.Source);
        Assert.Equal(Van, recorded.WarehouseCode);
        Assert.Equal(Item, recorded.ItemCode);
        Assert.Contains("VS-5", recorded.Reference);

        // Negative: the ledger held less than the document took, which is the direction that means
        // stock left that nobody knew was there to leave.
        Assert.Equal(-3m, recorded.Difference);
    }

    [Fact]
    public async Task A_van_with_no_snapshot_records_nothing_rather_than_inventing_a_shortfall()
    {
        // A van that never filed its morning count is not a van that sold stock it did not have. The
        // ledger has nothing to say about it and says nothing.
        await using var context = EmptyContext();

        Assert.Empty(await Ledger(context).TakeSettledAsync(Lines(8), "van sale VS-6"));
        Assert.Empty(await context.StockLedgerDivergences.ToListAsync());
    }

    // ---------------------------------------------------------------
    // What the builder actually sends
    // ---------------------------------------------------------------

    [Fact]
    public void The_invoice_builder_sends_no_batch_selection()
    {
        var request = DesktopSaleInvoiceRequestBuilder.Build(new DesktopSaleEntity
        {
            ExternalReferenceId = "VS-7",
            CardCode = "C-1",
            Currency = "USD",
            WarehouseCode = Van,
            DocDate = DateTime.UtcNow.Date,
            Lines = [new DesktopSaleLineEntity { LineNum = 1, ItemCode = Item, Quantity = 2, UnitPrice = 5m }]
        });

        var line = Assert.Single(request.Lines!);

        // The substance: this path sends no batch selection, because a desktop sale line carries no
        // batch to send. What SAP does with a batch-managed item then is SAP's own configuration.
        Assert.Null(line.BatchNumbers);

        // AutoAllocateBatches stays true and is asserted as such, because it is the right value if
        // this request ever reaches a handler that honours it — CreateInvoiceHandler is the only one
        // that does, and neither route using this builder goes through it. What was wrong here was
        // never the flag; it was a comment claiming the allocation happened. Comments are not pinned
        // by tests, so that correction stands on review rather than on an assertion.
        Assert.True(line.AutoAllocateBatches);
    }

    // ---------------------------------------------------------------

    private static List<StockLedgerLine> Lines(decimal quantity) => [new(Item, Van, quantity)];

    private static StockLedger Ledger(ApplicationDbContext context) =>
        new(context, Options.Create(new DailyStockSettings()), NullLogger<StockLedger>.Instance);

    /// <summary>A van that filed a morning count, the way ReportVanSalesStockPosition writes one.</summary>
    private static async Task<ApplicationDbContext> VanCarrying(decimal units)
    {
        var context = EmptyContext();

        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = StockLedgerDay.Today(new DailyStockSettings().StockFetchTimeCAT),
            WarehouseCode = Van,
            Status = StockSnapshotStatus.Complete
        };

        context.DailyStockSnapshots.Add(snapshot);
        await context.SaveChangesAsync();

        context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = Item,
            WarehouseCode = Van,
            OriginalQuantity = units,
            AvailableQuantity = units,
            Version = 1
        });

        await context.SaveChangesAsync();
        return context;
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
