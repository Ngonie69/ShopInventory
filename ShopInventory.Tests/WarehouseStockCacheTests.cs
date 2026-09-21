using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ShopInventory.Tests;

/// <summary>
/// The warehouse stock cache is what /products reads, so these pin the three things
/// the page states about a warehouse: what an item's four quantities are, what a
/// barcode resolves to, and how many items the warehouse actually holds.
/// </summary>
public sealed class WarehouseStockCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<WebAppDbContext> _options;

    public WarehouseStockCacheTests()
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

    // ── Quantities ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Paged_stock_carries_the_on_order_quantity()
    {
        // Availability is InStock - Committed + Ordered. Dropping Ordered leaves the
        // page showing available (4,192) above in stock (4,191) with committed at
        // zero and no term on the row to account for the difference.
        await SeedAsync(new CachedWarehouseStock
        {
            ItemCode = "ICC001",
            ItemName = "Icecream Cone Choc Dip",
            WarehouseCode = "KEFSHOP",
            InStock = 4191,
            Committed = 0,
            Ordered = 1,
            Available = 4192,
            UoM = "Each"
        });

        var service = CreateService();
        var response = await service.GetCachedStockAsync("KEFSHOP", 1, 20);

        var product = Assert.Single(response!.Products!);
        Assert.Equal(4191m, product.QuantityInStock);
        Assert.Equal(0m, product.QuantityCommitted);
        Assert.Equal(1m, product.QuantityOrdered);
        Assert.Equal(4192m, product.QuantityAvailable);

        Assert.Equal(
            product.QuantityAvailable,
            product.QuantityInStock - product.QuantityCommitted + product.QuantityOrdered);
    }

    // ── Barcode ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Barcode_lookup_finds_the_item_in_its_warehouse()
    {
        await SeedAsync(
            Stock("KEFSHOP", "ICC001", barCode: "6001234567890"),
            Stock("KEFSHOP", "ICS001", barCode: "6009876543210"));

        var service = CreateService();
        var found = await service.FindByBarcodeAsync("KEFSHOP", "6009876543210");

        Assert.Equal("ICS001", found?.ItemCode);
    }

    [Fact]
    public async Task Barcode_lookup_ignores_case_and_surrounding_space()
    {
        await SeedAsync(Stock("KEFSHOP", "ICC001", barCode: "AbC-123"));

        var service = CreateService();

        Assert.Equal("ICC001", (await service.FindByBarcodeAsync("KEFSHOP", "  abc-123 "))?.ItemCode);
    }

    [Fact]
    public async Task Barcode_lookup_falls_back_to_the_item_code()
    {
        // The same field takes a typed item code, which is how most of this page's
        // traffic uses it.
        await SeedAsync(Stock("KEFSHOP", "ICS003", barCode: null));

        var service = CreateService();

        Assert.Equal("ICS003", (await service.FindByBarcodeAsync("KEFSHOP", "ICS003"))?.ItemCode);
    }

    [Fact]
    public async Task Barcode_lookup_does_not_cross_warehouses()
    {
        await SeedAsync(Stock("MAIN", "ICC001", barCode: "6001234567890"));

        var service = CreateService();

        Assert.Null(await service.FindByBarcodeAsync("KEFSHOP", "6001234567890"));
    }

    [Fact]
    public async Task Barcode_lookup_matches_exactly_rather_than_by_prefix()
    {
        // A scan that resolved to "something similar" would be worse than no result.
        await SeedAsync(Stock("KEFSHOP", "ICC001", barCode: "6001234567890"));

        var service = CreateService();

        Assert.Null(await service.FindByBarcodeAsync("KEFSHOP", "600123"));
    }

    // ── Warehouse-wide search ───────────────────────────────────────────────

    [Fact]
    public async Task Search_reaches_rows_beyond_the_page_in_hand()
    {
        // The old page filtered only the rows already loaded, so an item on page 3
        // read as absent from the warehouse.
        await SeedAsync(Enumerable.Range(1, 40)
            .Select(i => Stock("KEFSHOP", $"ITEM{i:D3}", name: i == 39 ? "Vanilla Cone" : $"Item {i}"))
            .ToArray());

        var service = CreateService();
        var response = await service.GetCachedStockAsync("KEFSHOP", 1, 20, "vanilla");

        Assert.Equal(1, response!.Count);
        Assert.Equal("ITEM039", Assert.Single(response.Products!).ItemCode);
    }

    [Fact]
    public async Task Search_counts_and_rows_describe_the_same_set()
    {
        // Item codes deliberately free of the search term: the match has to come
        // from the name column alone, so the count is not inflated by the code one.
        await SeedAsync(Enumerable.Range(1, 30)
            .Select(i => Stock("KEFSHOP", $"ITM{i:D3}", name: i <= 25 ? "Cone" : "Tub"))
            .ToArray());

        var service = CreateService();
        var page = await service.GetCachedStockAsync("KEFSHOP", 1, 10, "cone");

        // 25 match; the page holds 10 of them and says so.
        Assert.Equal(25, page!.Count);
        Assert.Equal(10, page.Products!.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task Search_matching_nothing_answers_without_calling_the_api()
    {
        await SeedAsync(Stock("KEFSHOP", "ICC001", name: "Icecream Cone"));

        // Any HTTP call fails the test: an empty result for a cached warehouse is an
        // answer, not a cache miss to be refetched.
        var service = CreateService(new ThrowingHandler());
        var response = await service.GetCachedStockAsync("KEFSHOP", 1, 20, "nothing-matches-this");

        Assert.Equal(0, response!.Count);
        Assert.Empty(response.Products!);
    }

    // ── Summary ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_counts_the_whole_warehouse_not_one_page()
    {
        await SeedAsync(
            Stock("KEFSHOP", "A", inStock: 10, committed: 2, ordered: 0),
            Stock("KEFSHOP", "B", inStock: 0, committed: 0, ordered: 5),
            Stock("KEFSHOP", "C", inStock: 3, committed: 0, ordered: 0),
            Stock("KEFSHOP", "D", inStock: 0, committed: 1, ordered: 0),
            Stock("MAIN", "E", inStock: 99, committed: 9, ordered: 9));

        var service = CreateService();
        var summary = await service.GetStockSummaryAsync("KEFSHOP");

        Assert.Equal(4, summary.TotalItems);
        Assert.Equal(2, summary.InStockItems);
        Assert.Equal(2, summary.OutOfStockItems);
        Assert.Equal(2, summary.CommittedItems);
        Assert.Equal(1, summary.OnOrderItems);
    }

    [Fact]
    public async Task Summary_of_an_uncached_warehouse_is_zero_rather_than_a_failure()
    {
        var service = CreateService();
        var summary = await service.GetStockSummaryAsync("NOWHERE");

        Assert.Equal(0, summary.TotalItems);
        Assert.Equal(0, summary.OutOfStockItems);
    }

    // ── Sync ────────────────────────────────────────────────────────────────

    private const string WholeWarehouse = "/api/stock/warehouse/KEFSHOP?includePackagingStock=false";

    [Fact]
    public async Task A_full_sync_reads_the_whole_warehouse_in_one_request()
    {
        // It used to page through the API a hundred rows at a time, and the API answered each page
        // by running the warehouse's stock query on SAP again. One request here is one read there.
        var handler = new RecordingHandler(request =>
            request.RequestUri!.PathAndQuery == WholeWarehouse
                ? Json(new
                {
                    warehouseCode = "KEFSHOP",
                    totalItems = 2,
                    itemsInStock = 1,
                    queryDate = DateTime.UtcNow,
                    items = new[]
                    {
                        new { itemCode = "ICC001", itemName = "Icecream Cone", barCode = "6001", warehouseCode = "KEFSHOP", inStock = 3m, committed = 1m, ordered = 0m, available = 2m, uoM = "Each" },
                        new { itemCode = "ICS001", itemName = "Icecream Stick", barCode = "6002", warehouseCode = "KEFSHOP", inStock = 0m, committed = 0m, ordered = 0m, available = 0m, uoM = "Each" }
                    }
                })
                : throw new InvalidOperationException($"Unexpected call {request.RequestUri}"));

        var service = CreateService(handler);

        Assert.True(await service.SyncWarehouseStockAsync("KEFSHOP"));
        Assert.Equal(new[] { WholeWarehouse }, handler.Requests);

        // The zero-stock row is kept. The dashboard's out-of-stock count and the transfer pages'
        // item pickers read it from here, and an item missing from the cache is looked up on SAP
        // per item — so filtering it out at source would cost more than it saved.
        var summary = await service.GetStockSummaryAsync("KEFSHOP");
        Assert.Equal(2, summary.TotalItems);
        Assert.Equal(1, summary.InStockItems);
        Assert.Equal(1, summary.OutOfStockItems);

        var status = await service.GetSyncStatusAsync("KEFSHOP");
        Assert.True(status!.SyncSuccessful);
        Assert.Equal(2, status.ItemCount);
    }

    [Fact]
    public async Task A_read_that_fails_keeps_the_rows_it_has_and_is_not_an_empty_warehouse()
    {
        await SeedAsync(Stock("KEFSHOP", "ICC001", inStock: 3));
        var handler = new RecordingHandler(_ => throw new HttpRequestException("The API is not answering."));

        var service = CreateService(handler);

        Assert.False(await service.SyncWarehouseStockAsync("KEFSHOP"));

        // Still there, and still counted.
        Assert.Equal(1, (await service.GetStockSummaryAsync("KEFSHOP")).TotalItems);

        // And the failure is on record, so the next read tries again rather than serving the old
        // rows for the whole expiry as if they were fresh.
        var status = await service.GetSyncStatusAsync("KEFSHOP");
        Assert.False(status!.SyncSuccessful);
    }

    [Fact]
    public void Rows_are_served_for_fifteen_minutes_before_a_resync()
    {
        // Every expiry is the whole warehouse read from SAP again. Five minutes was the largest
        // share of this application's load on the HANA server on 2026-09-21.
        Assert.Equal(TimeSpan.FromMinutes(15), WarehouseStockCacheService.CacheExpiry);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CachedWarehouseStock Stock(
        string warehouseCode,
        string itemCode,
        string? name = null,
        string? barCode = null,
        decimal inStock = 1,
        decimal committed = 0,
        decimal ordered = 0) => new()
        {
            ItemCode = itemCode,
            ItemName = name ?? itemCode,
            BarCode = barCode,
            WarehouseCode = warehouseCode,
            InStock = inStock,
            Committed = committed,
            Ordered = ordered,
            Available = inStock - committed + ordered,
            UoM = "Each",
            LastSyncedAt = DateTime.UtcNow
        };

    private async Task SeedAsync(params CachedWarehouseStock[] rows)
    {
        await using var context = new WebAppDbContext(_options);
        context.CachedWarehouseStocks.AddRange(rows);

        // Fresh, so nothing schedules a background resync during a test.
        context.CacheSyncInfo.Add(new CacheSyncInfo
        {
            CacheKey = $"WarehouseStock_{rows[0].WarehouseCode}",
            LastSyncedAt = DateTime.UtcNow,
            ItemCount = rows.Length,
            SyncSuccessful = true
        });

        await context.SaveChangesAsync();
    }

    private WarehouseStockCacheService CreateService(HttpMessageHandler? handler = null)
    {
        var httpClient = new HttpClient(handler ?? new ThrowingHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };

        return new WarehouseStockCacheService(
            new TestDbContextFactory(_options),
            httpClient,
            NullLogger<WarehouseStockCacheService>.Instance);
    }

    private sealed class TestDbContextFactory(DbContextOptions<WebAppDbContext> options)
        : IDbContextFactory<WebAppDbContext>
    {
        public WebAppDbContext CreateDbContext() => new(options);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                $"The cache should have answered without calling {request.RequestUri}.");
    }

    /// <summary>Answers each request from <paramref name="respond"/> and keeps the paths it was asked for.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
}
