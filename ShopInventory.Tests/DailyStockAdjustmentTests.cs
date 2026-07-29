using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the daily stock snapshot being adjusted as transfers move stock between monitored
/// warehouses.
/// </summary>
/// <remarks>
/// These adjustments were being computed and then thrown away: the handler read the snapshot rows,
/// changed them and saved, but the context was registered to return untracked entities, so
/// SaveChangesAsync had nothing to write and said nothing about it. The snapshot silently drifted
/// from the stock it was supposed to describe. Every assertion here reads the rows back out of
/// storage rather than trusting the in-memory objects, because trusting them is what hid it.
/// </remarks>
public sealed class DailyStockAdjustmentTests : IDisposable
{
    private const string Source = "WH01";
    private const string Destination = "WH02";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public DailyStockAdjustmentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
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
    public async Task Stock_leaving_a_monitored_warehouse_is_deducted_in_storage()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);

        var result = await Handler().Handle(Transfer("ITEM-1", 4m), default);

        Assert.False(result.IsError);
        Assert.Equal(6m, await AvailableAsync(Source, "ITEM-1"));
    }

    [Fact]
    public async Task Stock_arriving_in_a_monitored_warehouse_is_added_in_storage()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 10m);
        await AddSnapshotItemAsync(Destination, "ITEM-1", 2m);

        await Handler().Handle(Transfer("ITEM-1", 4m), default);

        Assert.Equal(6m, await AvailableAsync(Source, "ITEM-1"));
        Assert.Equal(6m, await AvailableAsync(Destination, "ITEM-1"));
    }

    [Fact]
    public async Task An_outbound_transfer_draws_down_the_earliest_expiring_batch_first()
    {
        await AddSnapshotItemAsync(Source, "ITEM-1", 3m, new DateTime(2026, 8, 1));
        await AddSnapshotItemAsync(Source, "ITEM-1", 5m, new DateTime(2026, 9, 1));

        await Handler().Handle(Transfer("ITEM-1", 4m), default);

        var rows = await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(item => item.WarehouseCode == Source)
            .OrderBy(item => item.ExpiryDate)
            .ToListAsync();

        Assert.Equal(0m, rows[0].AvailableQuantity);
        Assert.Equal(4m, rows[1].AvailableQuantity);
    }

    [Fact]
    public async Task A_transfer_between_unmonitored_warehouses_changes_nothing()
    {
        await AddSnapshotItemAsync("WH99", "ITEM-1", 10m);

        var result = await Handler().Handle(
            new ProcessTransferEventCommand("ITEM-1", "WH98", "WH99", 4m, 1, 1), default);

        Assert.False(result.IsError);
        Assert.False(result.Value.Adjusted);
        Assert.Equal(10m, await AvailableAsync("WH99", "ITEM-1"));
    }

    // ── Helpers ─────────────────────────────────────────

    private ProcessTransferEventHandler Handler() => new(
        _context,
        Options.Create(new DailyStockSettings { MonitoredWarehouses = [Source, Destination] }),
        NullLogger<ProcessTransferEventHandler>.Instance);

    private static ProcessTransferEventCommand Transfer(string itemCode, decimal quantity)
        => new(itemCode, Source, Destination, quantity, 4001, 4001);

    private async Task<decimal> AvailableAsync(string warehouse, string itemCode)
        => await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(item => item.WarehouseCode == warehouse && item.ItemCode == itemCode)
            .SumAsync(item => item.AvailableQuantity);

    private async Task AddSnapshotItemAsync(
        string warehouse,
        string itemCode,
        decimal available,
        DateTime? expiryDate = null)
    {
        var today = DateTime.UtcNow.Date;
        var snapshot = await _context.DailyStockSnapshots
            .FirstOrDefaultAsync(item => item.SnapshotDate == today && item.WarehouseCode == warehouse);
        if (snapshot is null)
        {
            snapshot = new DailyStockSnapshotEntity { SnapshotDate = today, WarehouseCode = warehouse };
            _context.DailyStockSnapshots.Add(snapshot);
            await _context.SaveChangesAsync();
        }

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = itemCode,
            WarehouseCode = warehouse,
            OriginalQuantity = available,
            AvailableQuantity = available,
            ExpiryDate = expiryDate
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>
    /// <see cref="DailyStockSnapshotItemEntity.Version"/> is <c>[Timestamp]</c>, which Npgsql maps
    /// to the store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the
    /// column out of the INSERT and the NOT NULL constraint fails. Making it an ordinary property
    /// lets the fixture supply one; nothing under test reads it.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DailyStockSnapshotItemEntity>()
                .Property(item => item.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
