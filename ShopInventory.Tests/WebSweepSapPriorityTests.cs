using System.Collections.Concurrent;
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
/// The Web's cache services walk the API's /paged endpoints from fire-and-forget sweeps. Those
/// sweeps must say they are background work, or the API gives them the SAP slots it keeps for
/// people; the first page a person is waiting on must not.
/// </summary>
public sealed class WebSweepSapPriorityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<WebAppDbContext> _options;

    public WebSweepSapPriorityTests()
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
    public async Task Handler_marks_only_requests_made_inside_a_background_scope()
    {
        var recorder = new RecordingHandler(_ => Json(new { }));
        using var client = CreateClient(recorder);

        await client.GetAsync("api/first");
        using (SapBackgroundPriority.Begin())
        {
            await client.GetAsync("api/sweep");
        }

        await client.GetAsync("api/after");

        Assert.Null(recorder.PriorityOf("api/first"));
        Assert.Equal("background", recorder.PriorityOf("api/sweep"));
        Assert.Null(recorder.PriorityOf("api/after"));
    }

    [Fact]
    public async Task Run_marks_the_work_it_starts_and_not_the_caller()
    {
        var recorder = new RecordingHandler(_ => Json(new { }));
        using var client = CreateClient(recorder);

        await SapBackgroundPriority.Run(() => client.GetAsync("api/sweep"));
        await client.GetAsync("api/caller");

        Assert.Equal("background", recorder.PriorityOf("api/sweep"));
        Assert.Null(recorder.PriorityOf("api/caller"));
        Assert.False(SapBackgroundPriority.IsBackground);
    }

    [Fact]
    public async Task Transfer_sweep_is_background_and_the_awaited_first_page_is_not()
    {
        // An empty cache: the page awaits page 1 from the API, then a sweep fetches the rest.
        var warehouse = $"WH{Guid.NewGuid():N}"[..10];
        var recorder = new RecordingHandler(request =>
        {
            var query = request.RequestUri!.Query;
            var page = query.Contains("page=1&") ? 1 : 2;
            var transfers = Enumerable.Range(page * 1000, page == 1 ? 20 : 5)
                .Select(docEntry => new
                {
                    docEntry,
                    docNum = docEntry,
                    docDate = "2026-09-18",
                    fromWarehouse = warehouse,
                    toWarehouse = "MAIN"
                })
                .ToList();

            return Json(new
            {
                warehouse,
                page,
                pageSize = page == 1 ? 20 : 100,
                count = transfers.Count,
                totalCount = 25,
                totalPages = 2,
                hasMore = page == 1,
                transfers
            });
        });

        using var client = CreateClient(recorder);
        var service = new InventoryTransferCacheService(
            new TestDbContextFactory(_options),
            client,
            NullLogger<InventoryTransferCacheService>.Instance);

        var sweepFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SyncCompleted += (_, _) => sweepFinished.TrySetResult();

        var firstPage = await service.GetCachedTransfersAsync(warehouse, 1, 20);
        Assert.Equal(20, firstPage!.Transfers!.Count);

        await sweepFinished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var firstPath = $"api/inventorytransfer/{warehouse}/paged?page=1&pageSize=20";
        var sweepPath = $"api/inventorytransfer/{warehouse}/paged?page=2&pageSize=100";
        Assert.True(recorder.Saw(firstPath));
        Assert.True(recorder.Saw(sweepPath));
        Assert.Null(recorder.PriorityOf(firstPath));
        Assert.Equal("background", recorder.PriorityOf(sweepPath));
    }

    private static HttpClient CreateClient(HttpMessageHandler inner) =>
        new(new SapBackgroundPriorityHandler { InnerHandler = inner })
        {
            BaseAddress = new Uri("http://localhost/")
        };

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, string?> _priorityByPath = new();

        public bool Saw(string pathAndQuery) => _priorityByPath.ContainsKey(pathAndQuery);

        public string? PriorityOf(string pathAndQuery) =>
            _priorityByPath.TryGetValue(pathAndQuery, out var priority)
                ? priority
                : throw new InvalidOperationException($"No request was made to {pathAndQuery}.");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery.TrimStart('/');
            _priorityByPath[key] = request.Headers.TryGetValues(SapBackgroundPriority.HeaderName, out var values)
                ? string.Join(",", values)
                : null;

            return Task.FromResult(respond(request));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WebAppDbContext> options)
        : IDbContextFactory<WebAppDbContext>
    {
        public WebAppDbContext CreateDbContext() => new(options);
    }
}
