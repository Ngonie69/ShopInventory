using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// How much SAP work the Web's payment, transfer and warehouse stock caches cause when they refresh.
/// </summary>
/// <remarks>
/// On 2026-09-27 the payments cache re-read every incoming payment ever made whenever it was five
/// minutes old, the transfers cache did the same per warehouse, and every stale read queued a refresh
/// of its own that never checked whether the one ahead of it had already done the work.
/// </remarks>
public sealed class WebCacheSweepTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    // A file rather than one shared in-memory connection: a refresh runs in the background while the test
    // reads, and two contexts on one SqliteConnection at once fail with "database is locked".
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"web-cache-sweep-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<WebAppDbContext> _options;

    public WebCacheSweepTests()
    {
        _options = new DbContextOptionsBuilder<WebAppDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False;Default Timeout=30")
            .Options;

        using var context = new WebAppDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    // ── When a refresh is due ───────────────────────────────────────────────

    [Fact]
    public void A_failed_refresh_is_retried_after_a_short_wait_not_by_every_read()
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var expiry = TimeSpan.FromMinutes(5);

        Assert.True(CacheRefresh.IsDue(null, expiry, now));
        Assert.False(CacheRefresh.IsDue(Info(now.AddMinutes(-4), ok: true), expiry, now));
        Assert.True(CacheRefresh.IsDue(Info(now.AddMinutes(-6), ok: true), expiry, now));
        Assert.False(CacheRefresh.IsDue(Info(now.AddMinutes(-1), ok: false), expiry, now));
        Assert.True(CacheRefresh.IsDue(Info(now.AddMinutes(-3), ok: false), expiry, now));

        static CacheSyncInfo Info(DateTime at, bool ok) => new() { CacheKey = "k", LastSyncedAt = at, SyncSuccessful = ok };
    }

    // ── Payments ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_payments_refresh_reads_only_what_is_newer_than_the_cache()
    {
        var api = new PaymentsApi(lastDocEntry: 160);
        var service = Payments(api);
        await SeedPaymentsAsync(1, 150, lastSyncedMinutesAgo: 10);
        await SeedMarkAsync(PaymentsKey + ":Watermark", 150);

        Assert.True(await service.SyncPaymentsAsync());

        // Page one runs from 160 down to 61, so it reaches the cache; nothing older is asked for.
        Assert.Equal(["/api/incomingpayment?page=1&pageSize=100"], api.Requests);
        Assert.Equal(160, await CountPaymentsAsync());
    }

    [Fact]
    public async Task An_empty_payments_cache_is_walked_once_and_then_only_topped_up()
    {
        var api = new PaymentsApi(lastDocEntry: 250);
        var service = Payments(api);

        Assert.True(await service.SyncPaymentsAsync());
        Assert.Equal(3, api.Requests.Count);
        Assert.Equal(250, await CountPaymentsAsync());

        api.Requests.Clear();
        api.LastDocEntry = 255;

        Assert.True(await service.SyncPaymentsAsync());
        Assert.Equal(["/api/incomingpayment?page=1&pageSize=100"], api.Requests);
        Assert.Equal(255, await CountPaymentsAsync());
    }

    [Fact]
    public async Task A_payment_opened_on_its_own_does_not_hide_the_ones_before_it()
    {
        // Opening payment 150 directly caches it ahead of the refresh. Reading only past the highest
        // cached number would then skip 101 to 149 for good.
        var api = new PaymentsApi(lastDocEntry: 160);
        var service = Payments(api);
        await SeedPaymentsAsync(1, 100, lastSyncedMinutesAgo: 10);
        await SeedMarkAsync(PaymentsKey + ":Watermark", 100);
        await SeedPaymentsAsync(150, 150, lastSyncedMinutesAgo: 10);

        Assert.True(await service.SyncPaymentsAsync());

        Assert.Equal(160, await CountPaymentsAsync());
    }

    [Fact]
    public async Task A_payments_refresh_that_cannot_find_the_cache_walks_everything()
    {
        // More new payments than a top-up will page through: rather than stop with a gap, it reads
        // them all, as the first refresh did.
        var newest = 100 + (IncomingPaymentCacheService.TopUpPageLimit + 1) * 100;
        var api = new PaymentsApi(lastDocEntry: newest);
        var service = Payments(api);
        await SeedPaymentsAsync(1, 100, lastSyncedMinutesAgo: 10);
        await SeedMarkAsync(PaymentsKey + ":Watermark", 100);

        Assert.True(await service.SyncPaymentsAsync());

        Assert.Equal(newest, await CountPaymentsAsync());
    }

    [Fact]
    public async Task Stale_payment_reads_during_a_refresh_start_no_second_one()
    {
        var api = new PaymentsApi(lastDocEntry: 12)
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var service = Payments(api);
        await SeedPaymentsAsync(1, 10, lastSyncedMinutesAgo: 10);
        await SeedMarkAsync(PaymentsKey + ":Watermark", 10);

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SyncCompleted += (_, _) => finished.TrySetResult();

        for (var read = 0; read < 5; read++)
        {
            Assert.Equal(10, (await service.GetCachedPaymentsAsync(1, 20))!.Payments!.Count);
        }

        api.Gate!.SetResult();
        await finished.Task.WaitAsync(WaitLimit);
        await Task.Delay(500);

        Assert.Single(api.Requests);
    }

    // ── Transfers ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_transfers_refresh_reads_only_what_is_newer_than_the_cache()
    {
        var warehouse = NewWarehouse();
        var api = new TransfersApi(warehouse, lastDocEntry: 160);
        var service = Transfers(api);
        await SeedTransfersAsync(warehouse, 1, 150);
        await SeedMarkAsync($"InventoryTransfers_{warehouse}:Watermark", 150);

        Assert.True(await service.SyncTransfersAsync(warehouse));

        Assert.Equal([$"/api/inventorytransfer/{warehouse}/paged?page=1&pageSize=100"], api.Requests);
        Assert.Equal(160, await CountTransfersAsync(warehouse));
    }

    [Fact]
    public async Task An_empty_transfers_cache_is_walked_once_and_then_only_topped_up()
    {
        var warehouse = NewWarehouse();
        var api = new TransfersApi(warehouse, lastDocEntry: 250);
        var service = Transfers(api);

        Assert.True(await service.SyncTransfersAsync(warehouse));
        Assert.Equal(3, api.Requests.Count);

        api.Requests.Clear();
        api.LastDocEntry = 252;

        Assert.True(await service.SyncTransfersAsync(warehouse));
        Assert.Single(api.Requests);
        Assert.Equal(252, await CountTransfersAsync(warehouse));
    }

    // ── Warehouse stock ─────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_stock_reads_during_a_refresh_start_no_second_one()
    {
        var warehouse = NewWarehouse();
        var api = new StockApi
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var service = Stock(api);
        await SeedStockAsync(warehouse, lastSyncedMinutesAgo: 30);

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SyncCompleted += (_, _) => finished.TrySetResult();

        for (var read = 0; read < 5; read++)
        {
            await service.GetCachedStockAsync(warehouse, 1, 20);
        }

        api.Gate!.SetResult();
        await finished.Task.WaitAsync(WaitLimit);
        await Task.Delay(500);

        Assert.Single(api.RequestsFor(warehouse));
    }

    [Fact]
    public async Task One_warehouse_that_hangs_does_not_hold_up_another()
    {
        var stuck = NewWarehouse();
        var other = NewWarehouse();
        var api = new StockApi { Hangs = stuck };
        var service = Stock(api);
        await SeedStockAsync(stuck, lastSyncedMinutesAgo: 30);
        await SeedStockAsync(other, lastSyncedMinutesAgo: 30);

        var otherDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SyncCompleted += (_, warehouse) =>
        {
            if (warehouse == other)
                otherDone.TrySetResult();
        };

        await service.GetCachedStockAsync(stuck, 1, 20);
        await api.WaitForHangAsync().WaitAsync(WaitLimit);
        await service.GetCachedStockAsync(other, 1, 20);

        await otherDone.Task.WaitAsync(WaitLimit);
        api.ReleaseHang();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private const string PaymentsKey = "IncomingPayments";

    private static string NewWarehouse() => $"W{Guid.NewGuid():N}"[..10];

    private IncomingPaymentCacheService Payments(HttpMessageHandler api) =>
        new(new TestDbContextFactory(_options), Client(api), NullLogger<IncomingPaymentCacheService>.Instance);

    private InventoryTransferCacheService Transfers(HttpMessageHandler api) =>
        new(new TestDbContextFactory(_options), Client(api), NullLogger<InventoryTransferCacheService>.Instance);

    private WarehouseStockCacheService Stock(HttpMessageHandler api) =>
        new(new TestDbContextFactory(_options), Client(api), NullLogger<WarehouseStockCacheService>.Instance);

    private static HttpClient Client(HttpMessageHandler api) => new(api) { BaseAddress = new Uri("http://localhost/") };

    private async Task SeedPaymentsAsync(int from, int to, int lastSyncedMinutesAgo)
    {
        await using var context = new WebAppDbContext(_options);
        for (var docEntry = from; docEntry <= to; docEntry++)
        {
            context.CachedIncomingPayments.Add(new CachedIncomingPayment
            {
                DocEntry = docEntry,
                DocNum = docEntry,
                DocDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                LastSyncedAt = DateTime.UtcNow
            });
        }

        await UpsertSyncInfoAsync(context, PaymentsKey, DateTime.UtcNow.AddMinutes(-lastSyncedMinutesAgo), to);
        await context.SaveChangesAsync();
    }

    private async Task SeedTransfersAsync(string warehouse, int from, int to)
    {
        await using var context = new WebAppDbContext(_options);
        for (var docEntry = from; docEntry <= to; docEntry++)
        {
            context.CachedInventoryTransfers.Add(new CachedInventoryTransfer
            {
                DocEntry = docEntry,
                DocNum = docEntry,
                DocDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                FromWarehouse = warehouse,
                ToWarehouse = "MAIN",
                LastSyncedAt = DateTime.UtcNow
            });
        }

        await UpsertSyncInfoAsync(context, $"InventoryTransfers_{warehouse}", DateTime.UtcNow.AddMinutes(-10), to - from + 1);
        await context.SaveChangesAsync();
    }

    private async Task SeedStockAsync(string warehouse, int lastSyncedMinutesAgo)
    {
        await using var context = new WebAppDbContext(_options);
        context.CachedWarehouseStocks.Add(new CachedWarehouseStock
        {
            ItemCode = "ICC001",
            ItemName = "Icecream Cone",
            WarehouseCode = warehouse,
            InStock = 3,
            Available = 3,
            UoM = "Each",
            LastSyncedAt = DateTime.UtcNow
        });

        await UpsertSyncInfoAsync(context, $"WarehouseStock_{warehouse}", DateTime.UtcNow.AddMinutes(-lastSyncedMinutesAgo), 1);
        await context.SaveChangesAsync();
    }

    private async Task SeedMarkAsync(string key, int docEntry)
    {
        await using var context = new WebAppDbContext(_options);
        await UpsertSyncInfoAsync(context, key, DateTime.UtcNow.AddMinutes(-10), docEntry);
        await context.SaveChangesAsync();
    }

    private static async Task UpsertSyncInfoAsync(WebAppDbContext context, string key, DateTime at, int count)
    {
        var row = await context.CacheSyncInfo.FindAsync(key);
        if (row is null)
        {
            row = new CacheSyncInfo { CacheKey = key };
            context.CacheSyncInfo.Add(row);
        }

        row.LastSyncedAt = at;
        row.ItemCount = count;
        row.SyncSuccessful = true;
    }

    private async Task<int> CountPaymentsAsync()
    {
        await using var context = new WebAppDbContext(_options);
        return await context.CachedIncomingPayments.Select(p => p.DocEntry).Distinct().CountAsync();
    }

    private async Task<int> CountTransfersAsync(string warehouse)
    {
        await using var context = new WebAppDbContext(_options);
        return await context.CachedInventoryTransfers
            .Where(t => t.FromWarehouse == warehouse || t.ToWarehouse == warehouse)
            .Select(t => t.DocEntry)
            .Distinct()
            .CountAsync();
    }

    private static int PageOf(HttpRequestMessage request) =>
        int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);

    private static IEnumerable<int> NewestFirst(int lastDocEntry, int page, int pageSize = 100) =>
        Enumerable.Range(1, lastDocEntry).Reverse().Skip((page - 1) * pageSize).Take(pageSize);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    /// <summary>SAP's incoming payments, DocEntry 1 to <see cref="LastDocEntry"/>, newest first as the API pages them.</summary>
    private sealed class PaymentsApi(int lastDocEntry) : HttpMessageHandler
    {
        public int LastDocEntry { get; set; } = lastDocEntry;
        public TaskCompletionSource? Gate { get; init; }
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.PathAndQuery);

            if (Gate is not null)
                await Gate.Task;

            var page = PageOf(request);
            var payments = NewestFirst(LastDocEntry, page)
                .Select(docEntry => new { docEntry, docNum = docEntry, docDate = "2026-09-01" })
                .ToList();

            return Json(new { page, pageSize = 100, count = payments.Count, hasMore = payments.Count == 100, payments });
        }
    }

    /// <summary>One warehouse's stock transfers, DocEntry 1 to <see cref="LastDocEntry"/>, newest first.</summary>
    private sealed class TransfersApi(string warehouse, int lastDocEntry) : HttpMessageHandler
    {
        public int LastDocEntry { get; set; } = lastDocEntry;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.PathAndQuery);

            var page = PageOf(request);
            var transfers = NewestFirst(LastDocEntry, page)
                .Select(docEntry => new { docEntry, docNum = docEntry, docDate = "2026-09-01", fromWarehouse = warehouse, toWarehouse = "MAIN" })
                .ToList();

            return Task.FromResult(Json(new
            {
                warehouse,
                page,
                pageSize = 100,
                count = transfers.Count,
                totalCount = LastDocEntry,
                hasMore = page * 100 < LastDocEntry,
                transfers
            }));
        }
    }

    /// <summary>The whole-warehouse stock read, optionally held until released.</summary>
    private sealed class StockApi : HttpMessageHandler
    {
        private readonly TaskCompletionSource _hung = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _requests = [];

        public TaskCompletionSource? Gate { get; init; }
        public string? Hangs { get; init; }

        public Task WaitForHangAsync() => _hung.Task;
        public void ReleaseHang() => _release.TrySetResult();

        public List<string> RequestsFor(string warehouse)
        {
            lock (_requests)
                return _requests.Where(path => path.Contains($"/{warehouse}?", StringComparison.Ordinal)).ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (_requests)
                _requests.Add(path);

            var warehouse = request.RequestUri.AbsolutePath.Split('/').Last();

            if (warehouse == Hangs)
            {
                _hung.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            if (Gate is not null)
                await Gate.Task;

            return Json(new
            {
                warehouseCode = warehouse,
                totalItems = 1,
                itemsInStock = 1,
                queryDate = DateTime.UtcNow,
                items = new[]
                {
                    new { itemCode = "ICC001", itemName = "Icecream Cone", warehouseCode = warehouse, inStock = 4m, committed = 0m, ordered = 0m, available = 4m, uoM = "Each" }
                }
            });
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WebAppDbContext> options)
        : IDbContextFactory<WebAppDbContext>
    {
        public WebAppDbContext CreateDbContext() => new(options);
    }
}
