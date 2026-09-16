using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the catalogue's price lists say when SAP was last read for each.
/// </summary>
/// <remarks>
/// The Price List page states "SAP sync … ago" from this. Left out of the projection, the field
/// arrives null and the page quietly drops the line, so nothing else would notice.
/// </remarks>
public sealed class PriceListSyncTimeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly LocalPriceCatalogService _service;

    public PriceListSyncTimeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _service = new LocalPriceCatalogService(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Price_lists_carry_their_last_sync_time()
    {
        var syncedAt = new DateTime(2026, 9, 16, 8, 30, 0, DateTimeKind.Utc);
        _context.PriceLists.AddRange(
            new PriceListEntity { ListNum = 26, ListName = "Cortina Shops 2026", Currency = "USD", LastSyncedAt = syncedAt },
            new PriceListEntity { ListNum = 27, ListName = "Never synced", Currency = "USD" });
        await _context.SaveChangesAsync();

        var response = await _service.GetPriceListsAsync();

        var synced = Assert.Single(response.PriceLists!, list => list.ListNum == 26);
        Assert.Equal(syncedAt, synced.LastSyncedAt);
        Assert.Null(Assert.Single(response.PriceLists!, list => list.ListNum == 27).LastSyncedAt);
    }
}
