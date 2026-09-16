using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The journal read across the two subsystems that move a warehouse's stock during the day.
/// </summary>
/// <remarks>
/// Sales take stock out through the ledger; transfers put it in through the listener's webhook. They
/// write the same cell and, until now, only one of them said so. That is what made
/// <c>AvailableQuantity</c> unaccountable: a figure three subsystems could change and none could
/// explain.
///
/// <para>The assertion these tests are really about is the last one in each: opening quantity plus
/// everything journalled equals the figure the guard reads. It is a statement about completeness,
/// so it only means anything when both writers are in the sum — which is why these live in one file
/// rather than being split between the two handlers' suites.</para>
/// </remarks>
public sealed class StockMovementInvariantTests : IDisposable
{
    private const string Source = "WH01";
    private const string Destination = "WH02";
    private const string Item = "ITEM-1";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private readonly DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = [Source, Destination]
    };

    public StockMovementInvariantTests()
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

    // ---------------------------------------------------------------
    // Transfers reach the journal
    // ---------------------------------------------------------------

    [Fact]
    public async Task Stock_arriving_by_transfer_is_journalled_as_a_movement_in()
    {
        await AddRowAsync(Destination, units: 2m);

        await Handler().Handle(Transfer(quantity: 5m), default);

        var movement = await MovementAsync(Destination);
        Assert.Equal(StockMovementKinds.Transfer, movement.Kind);
        Assert.Equal(5m, movement.Quantity);
        Assert.Equal(7m, movement.BalanceAfter);
        Assert.Equal("transfer:4001:IN", movement.DocumentKey);
    }

    [Fact]
    public async Task Stock_leaving_by_transfer_is_journalled_as_a_movement_out()
    {
        await AddRowAsync(Source, units: 10m);

        await Handler().Handle(Transfer(quantity: 4m), default);

        var movement = await MovementAsync(Source);
        Assert.Equal(-4m, movement.Quantity);
        Assert.Equal(6m, movement.BalanceAfter);
        Assert.Equal("transfer:4001:OUT", movement.DocumentKey);
    }

    [Fact]
    public async Task A_transfer_delivered_twice_is_journalled_once()
    {
        await AddRowAsync(Source, units: 10m);
        await AddRowAsync(Destination, units: 0m);

        // The listener re-reads a three-day window on every poll, so a repeat delivery is ordinary
        // operation rather than an edge case.
        await Handler().Handle(Transfer(quantity: 4m), default);
        _context.ChangeTracker.Clear();
        await Handler().Handle(Transfer(quantity: 4m), default);

        Assert.Equal(2, await _context.StockMovements.CountAsync());
        Assert.Equal(6m, await AvailableAsync(Source));
    }

    [Fact]
    public async Task A_transfer_out_of_a_warehouse_that_is_short_journals_what_actually_moved()
    {
        await AddRowAsync(Source, units: 3m);

        // SAP has already moved eight. The snapshot only ever knew about three, so three is what
        // leaves the ledger — and three is what the journal has to say, or the invariant breaks for
        // exactly the item that is already in trouble.
        await Handler().Handle(Transfer(quantity: 8m), default);

        var movement = await MovementAsync(Source);
        Assert.Equal(-3m, movement.Quantity);
        Assert.Equal(0m, movement.BalanceAfter);
        await AssertInvariantAsync(Source);
    }

    [Fact]
    public async Task A_transfer_into_an_item_with_no_row_yet_still_balances()
    {
        await AddSnapshotAsync(Destination);

        // Nothing of this item was in the warehouse at 07:00, so the row is born mid-day with an
        // opening quantity of zero. The invariant has to hold for it from the moment it exists.
        await Handler().Handle(Transfer(quantity: 6m), default);

        var movement = await MovementAsync(Destination);
        Assert.Equal(6m, movement.Quantity);
        Assert.Equal(6m, movement.BalanceAfter);
        await AssertInvariantAsync(Destination);
    }

    // ---------------------------------------------------------------
    // Both writers in one sum
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_sale_and_a_transfer_on_one_item_both_account_for_themselves()
    {
        await AddRowAsync(Destination, units: 10m, complete: true);

        // Ten on the shelf at 07:00. A transfer brings five more in, a till sells four.
        await Handler().Handle(Transfer(quantity: 5m), default);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var committed = await Ledger().TryCommitAsync(
            [new StockLedgerLine(Item, Destination, 4m)], "till sale", "desktop-sale:abc");

        Assert.True(committed.Committed);
        Assert.Equal(11m, await AvailableAsync(Destination));

        // Neither subsystem knows about the other. The journal is the only place the day's movements
        // meet, and this is the sum that says it is complete.
        await AssertInvariantAsync(Destination);

        var kinds = await _context.StockMovements
            .OrderBy(movement => movement.Id)
            .Select(movement => movement.Kind)
            .ToListAsync();
        Assert.Equal([StockMovementKinds.Transfer, StockMovementKinds.Commit], kinds);
    }

    // ---------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------

    /// <summary>
    /// Opening quantity plus everything journalled is the figure the guard reads. A writer that
    /// moves the cell without appending to the journal fails here and nowhere else.
    /// </summary>
    private async Task AssertInvariantAsync(string warehouse)
    {
        await _context.SaveChangesAsync();

        var opening = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.WarehouseCode == warehouse && row.ItemCode == Item)
            .SumAsync(row => row.OriginalQuantity);

        var journalled = await _context.StockMovements
            .AsNoTracking()
            .Where(movement => movement.WarehouseCode == warehouse && movement.ItemCode == Item)
            .SumAsync(movement => movement.Quantity);

        Assert.Equal(await AvailableAsync(warehouse), opening + journalled);
    }

    private ProcessTransferEventHandler Handler() => new(
        _context,
        Options.Create(_settings),
        NullLogger<ProcessTransferEventHandler>.Instance);

    private StockLedger Ledger() => new(
        _context,
        Options.Create(_settings),
        NullLogger<StockLedger>.Instance);

    private DateTime LedgerDay => StockLedgerDay.Today(_settings.StockFetchTimeCAT);

    private static ProcessTransferEventCommand Transfer(decimal quantity)
        => new(Item, Source, Destination, quantity, 4001, 4001);

    private async Task<StockMovementEntity> MovementAsync(string warehouse)
    {
        await _context.SaveChangesAsync();
        return await _context.StockMovements
            .AsNoTracking()
            .SingleAsync(movement => movement.WarehouseCode == warehouse);
    }

    private async Task<decimal> AvailableAsync(string warehouse)
        => await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.WarehouseCode == warehouse && row.ItemCode == Item)
            .SumAsync(row => row.AvailableQuantity);

    private async Task<DailyStockSnapshotEntity> AddSnapshotAsync(string warehouse, bool complete = false)
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
                // The ledger only sells from a warehouse whose snapshot completed; the transfer
                // handler does not care either way.
                Status = complete ? StockSnapshotStatus.Complete : default
            };

            _context.DailyStockSnapshots.Add(snapshot);
            await _context.SaveChangesAsync();
        }

        return snapshot;
    }

    private async Task AddRowAsync(string warehouse, decimal units, bool complete = false)
    {
        var snapshot = await AddSnapshotAsync(warehouse, complete);

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = Item,
            WarehouseCode = warehouse,
            OriginalQuantity = units,
            AvailableQuantity = units,
            // See SnapshotSqliteContext for why this is set by hand.
            Version = 1
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
