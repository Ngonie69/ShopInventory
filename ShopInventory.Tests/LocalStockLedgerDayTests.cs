using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetLocalStock;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the till's stock read asking for the snapshot day that is actually in force.
/// </summary>
/// <remarks>
/// <see cref="StockLedgerDay"/> was introduced to stop callers dating a snapshot by
/// <c>DateTime.UtcNow.Date</c>, which rolls at 02:00 CAT — five hours before the 07:00 fetch writes
/// the day it would then be asking for. Every caller was moved onto it except this one, the route
/// <c>GET /api/DesktopIntegration/stock/{warehouse}/local</c> that the KefShop till draws its whole
/// catalogue from. The till therefore showed "today's stock figures are not available yet" every
/// morning from 02:00 to 07:00, with the snapshot it should have been selling from sitting in
/// storage under the previous day's date.
///
/// <para>
/// The fetch time is the lever these tests pull rather than the clock: the day in force is a
/// function of both, so a configured fetch time of 23:59 puts the whole of any real test run before
/// the morning boundary, and 00:00 puts all of it after. That makes the two cases assertable at any
/// hour the suite happens to run — see <c>CaptureClockTests</c> for what a test pinned to a literal
/// date costs later.
/// </para>
/// </remarks>
public sealed class LocalStockLedgerDayTests : IDisposable
{
    private const string Warehouse = "KEFSHOP";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public LocalStockLedgerDayTests()
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

    [Fact]
    public async Task Before_the_morning_fetch_the_till_reads_the_snapshot_still_in_force()
    {
        // 23:59 makes every moment of this run "before the fetch", so the day in force is yesterday's.
        var inForce = StockLedgerDay.Today("23:59");
        await AddSnapshotAsync(inForce, "ITEM-1", 7m);

        var result = await Handler("23:59").Handle(new GetLocalStockQuery(Warehouse), default);

        Assert.False(result.IsError);
        Assert.Equal(inForce, result.Value.SnapshotDate);
        Assert.Equal(7m, Assert.Single(result.Value.Items).AvailableQuantity);
    }

    [Fact]
    public async Task After_the_morning_fetch_the_till_reads_the_day_that_replaced_it()
    {
        // 00:00 makes every moment of this run "after the fetch", so the day in force is the current one.
        var inForce = StockLedgerDay.Today("00:00");
        await AddSnapshotAsync(inForce, "ITEM-1", 4m);

        var result = await Handler("00:00").Handle(new GetLocalStockQuery(Warehouse), default);

        Assert.False(result.IsError);
        Assert.Equal(inForce, result.Value.SnapshotDate);
        Assert.Equal(4m, Assert.Single(result.Value.Items).AvailableQuantity);
    }

    [Fact]
    public async Task A_day_the_caller_names_is_still_the_day_it_gets()
    {
        var named = new DateTime(2026, 3, 4);
        await AddSnapshotAsync(named, "ITEM-1", 2m);

        var result = await Handler("07:00").Handle(new GetLocalStockQuery(Warehouse, named), default);

        Assert.False(result.IsError);
        Assert.Equal(named, result.Value.SnapshotDate);
    }

    [Fact]
    public async Task A_day_with_no_snapshot_is_reported_as_the_day_that_was_looked_for()
    {
        var inForce = StockLedgerDay.Today("23:59");

        var result = await Handler("23:59").Handle(new GetLocalStockQuery(Warehouse), default);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SnapshotNotFound", result.FirstError.Code);
        Assert.Contains(inForce.ToString("yyyy-MM-dd"), result.FirstError.Description);
    }

    private GetLocalStockHandler Handler(string fetchTimeCat) =>
        new(_context, Options.Create(new DailyStockSettings
        {
            StockFetchTimeCAT = fetchTimeCat,
            MonitoredWarehouses = [Warehouse]
        }));

    private async Task AddSnapshotAsync(DateTime snapshotDate, string itemCode, decimal quantity)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = snapshotDate,
            WarehouseCode = Warehouse,
            Status = StockSnapshotStatus.Complete,
            CreatedAt = DateTime.UtcNow
        };

        _context.DailyStockSnapshots.Add(snapshot);
        await _context.SaveChangesAsync();

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = itemCode,
            ItemDescription = itemCode,
            WarehouseCode = Warehouse,
            AvailableQuantity = quantity,
            OriginalQuantity = quantity,
            Version = 1
        });

        await _context.SaveChangesAsync();
    }
}
