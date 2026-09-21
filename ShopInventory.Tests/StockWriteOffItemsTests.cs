using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffItems;

namespace ShopInventory.Tests;

/// <summary>
/// The write-off page's item picker reads the Web's own PostgreSQL catalogue, never SAP. These pin
/// what that read answers: which rows, in what order, with the batch flag that decides whether the
/// batch picker appears, and what it says about a catalogue that has never synced.
/// </summary>
public sealed class StockWriteOffItemsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<WebAppDbContext> _options;

    public StockWriteOffItemsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<WebAppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new WebAppDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Answers_every_active_item_by_code_with_its_batch_flag()
    {
        await SeedAsync(
            Product("CHE011", "Cheddar Mild 250g", managesBatches: true),
            Product("CON020", "Container", managesBatches: false),
            Product("ABC001", "Aged Brie", managesBatches: true),
            Product("OLD001", "Discontinued line", managesBatches: true, isActive: false));

        var result = await new GetStockWriteOffItemsHandler(
                new TestDbContextFactory(_options),
                NullLogger<GetStockWriteOffItemsHandler>.Instance)
            .Handle(new GetStockWriteOffItemsQuery(), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);

        // Ordered by code, so the picker's unsearched list is stable; the inactive row is out,
        // because a write-off cannot name an item SAP no longer sells.
        Assert.Equal(["ABC001", "CHE011", "CON020"], result.Value.Items.Select(item => item.ItemCode));

        var cheddar = result.Value.Items.Single(item => item.ItemCode == "CHE011");
        Assert.Equal("Cheddar Mild 250g", cheddar.ItemName);
        Assert.True(cheddar.ManagesBatches);

        var container = result.Value.Items.Single(item => item.ItemCode == "CON020");
        Assert.False(container.ManagesBatches);
    }

    [Fact]
    public async Task Reports_when_the_catalogue_was_last_synced()
    {
        var syncedAt = new DateTime(2026, 9, 21, 6, 0, 0, DateTimeKind.Utc);
        await SeedAsync(Product("CHE011", "Cheddar Mild 250g", managesBatches: true));
        await using (var context = new WebAppDbContext(_options))
        {
            context.CacheSyncInfo.Add(new CacheSyncInfo
            {
                CacheKey = "Products_All",
                LastSyncedAt = syncedAt,
                ItemCount = 1,
                SyncSuccessful = true
            });
            await context.SaveChangesAsync();
        }

        var result = await new GetStockWriteOffItemsHandler(
                new TestDbContextFactory(_options),
                NullLogger<GetStockWriteOffItemsHandler>.Instance)
            .Handle(new GetStockWriteOffItemsQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(syncedAt, result.Value.SyncedAt);
    }

    [Fact]
    public async Task A_catalogue_that_never_synced_is_empty_and_undated_not_an_error()
    {
        // A fresh environment has no rows and no sync stamp. That is a state the page has to
        // explain, not a failure the handler should dress up as one.
        var result = await new GetStockWriteOffItemsHandler(
                new TestDbContextFactory(_options),
                NullLogger<GetStockWriteOffItemsHandler>.Instance)
            .Handle(new GetStockWriteOffItemsQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Empty(result.Value.Items);
        Assert.Null(result.Value.SyncedAt);
    }

    [Fact]
    public async Task A_failed_sync_stamp_does_not_count_as_a_sync()
    {
        await using (var context = new WebAppDbContext(_options))
        {
            context.CacheSyncInfo.Add(new CacheSyncInfo
            {
                CacheKey = "Products_All",
                LastSyncedAt = DateTime.UtcNow,
                ItemCount = 0,
                SyncSuccessful = false,
                LastError = "SAP timed out"
            });
            await context.SaveChangesAsync();
        }

        var result = await new GetStockWriteOffItemsHandler(
                new TestDbContextFactory(_options),
                NullLogger<GetStockWriteOffItemsHandler>.Instance)
            .Handle(new GetStockWriteOffItemsQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Null(result.Value.SyncedAt);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CachedProduct Product(string code, string name, bool managesBatches, bool isActive = true) => new()
    {
        ItemCode = code,
        ItemName = name,
        ItemType = "itItems",
        ManagesBatches = managesBatches,
        IsActive = isActive,
        LastSyncedAt = DateTime.UtcNow
    };

    private async Task SeedAsync(params CachedProduct[] products)
    {
        await using var context = new WebAppDbContext(_options);
        context.CachedProducts.AddRange(products);
        await context.SaveChangesAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<WebAppDbContext> options)
        : IDbContextFactory<WebAppDbContext>
    {
        public WebAppDbContext CreateDbContext() => new(options);
    }
}
