using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Middleware;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// How a catalogue page gets its batches: from the warehouse's last good whole read, refreshed behind the
/// page rather than in front of it.
/// </summary>
/// <remarks>
/// <para>
/// On 2026-11-12, 06:54–12:56, 65 of the 124 reads the Service Layer got stuck on were the van catalogue's
/// whole-warehouse batch read. A page that found the two-minute snapshot expired waited on a fresh read;
/// stuck, it failed after 60 s, nothing was kept, and the next page sent the same read again — 23 times
/// inside the stuck-read hold, because a person was waiting. 89 of the 90 failed pages had a good read of
/// the same van under an hour old.
/// </para>
/// <para>
/// These run the real client over a scripted Service Layer on a <see cref="ManualClock"/>. Each test uses
/// its own warehouse code: the in-flight read is shared process-wide by warehouse.
/// </para>
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class WarehouseBatchStaleSnapshotTests : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxStale = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly ManualClock _clock = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ScriptedServiceLayer _sap = new();
    private readonly string _warehouse = $"SNAP{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    public void Dispose()
    {
        _sap.ReleaseAll();
        _cache.Dispose();
    }

    // ── Inside the fresh window ─────────────────────────────────────────────

    [Fact]
    public async Task Inside_the_fresh_window_a_page_is_answered_from_the_read_without_asking_SAP()
    {
        var client = Client();
        var first = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);

        _clock.Advance(Fresh - TimeSpan.FromSeconds(1));
        var second = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);

        Assert.Equal(1, _sap.ListReads);
        Assert.False(second.IsStale);
        Assert.Equal(first.ReadAtUtc, second.ReadAtUtc);
    }

    [Fact]
    public async Task The_read_is_timed_from_when_it_was_sent()
    {
        var sentAt = _clock.GetUtcNow();

        var snapshot = await Client().GetWarehouseBatchesForDisplayAsync(_warehouse);

        Assert.Equal(sentAt, snapshot.ReadAtUtc);
        Assert.False(snapshot.IsStale);
    }

    // ── Past it: answered at once, refreshed behind ─────────────────────────

    [Fact]
    public async Task Past_the_fresh_window_a_page_is_answered_at_once_from_the_last_read_while_SAP_is_asked_again()
    {
        var client = Client();
        var first = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        _sap.Stocked = ["CHE011", "PIC003"];
        var gate = _sap.HangNextList();

        _clock.Advance(Fresh);
        var page = await client.GetWarehouseBatchesForDisplayAsync(_warehouse).WaitAsync(Wait);

        // Answered while SAP has not answered the refresh.
        Assert.True(page.IsStale);
        Assert.Equal(first.ReadAtUtc, page.ReadAtUtc);
        Assert.Equal(["CHE011", "NRI049"], page.StockedItemCodes);
        await _sap.WaitForListReadsAsync(2);

        gate.SetResult();
        var refreshed = await WaitForFreshAsync(client);

        Assert.Equal(first.ReadAtUtc + Fresh, refreshed.ReadAtUtc);
        Assert.Equal(["CHE011", "PIC003"], refreshed.StockedItemCodes);
        Assert.Equal(2, _sap.ListReads);
    }

    [Fact]
    public async Task Pages_that_find_the_read_stale_together_send_SAP_one_refresh()
    {
        var client = Client();
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        var gate = _sap.HangNextList();
        _clock.Advance(Fresh);

        var pages = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => client.GetWarehouseBatchesForDisplayAsync(_warehouse))))
            .WaitAsync(Wait);

        Assert.All(pages, page => Assert.True(page.IsStale));
        await _sap.WaitForListReadsAsync(2);
        Assert.Equal(2, _sap.ListReads);

        gate.SetResult();
        await WaitForFreshAsync(client);
        Assert.Equal(2, _sap.ListReads);
    }

    /// <summary>
    /// The refresh is background work whoever's page started it, so the stuck-read hold refuses a repeat of
    /// a read SAP is still working on and the read family's background limit applies to it.
    /// </summary>
    [Fact]
    public async Task The_refresh_goes_to_SAP_as_background_work_even_from_a_page_a_person_is_waiting_on()
    {
        var client = Client();
        using (SapRequestPriority.BeginInteractive())
        {
            await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
            _clock.Advance(Fresh);

            await client.GetWarehouseBatchesForDisplayAsync(_warehouse);

            // The page's own priority is untouched.
            Assert.True(SapRequestPriority.IsInteractive);
        }

        await _sap.WaitForListReadsAsync(2);
        Assert.Equal([true, false], _sap.Reads.Select(read => read.Interactive));
    }

    [Fact]
    public async Task Every_catalogue_read_of_the_batches_is_marked_display_only_and_a_movement_read_is_not()
    {
        var client = Client();
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        _clock.Advance(Fresh);
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        await _sap.WaitForListReadsAsync(2);
        await WaitForFreshAsync(client);

        await client.GetAllBatchNumbersInWarehouseAsync(_warehouse);

        Assert.Equal([true, true, false], _sap.Reads.Select(read => read.DisplayOnly));
    }

    // ── When the refresh does not answer ────────────────────────────────────

    [Fact]
    public async Task A_failed_refresh_leaves_the_last_good_read_answering_and_the_next_page_tries_again()
    {
        var client = Client();
        var first = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        _sap.FailNextList(HttpStatusCode.NotFound);
        _clock.Advance(Fresh);

        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        await _sap.WaitForListReadsAsync(2);
        await WaitUntilNoneInFlightAsync(client);

        var page = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);

        Assert.True(page.IsStale);
        Assert.Equal(first.ReadAtUtc, page.ReadAtUtc);
        await _sap.WaitForListReadsAsync(3);
    }

    /// <summary>
    /// SAP's pipeline refuses the refresh because SAP is presumed to be still working on the last one. Every
    /// page until the hold ends would only be refused again, so none is tried.
    /// </summary>
    [Fact]
    public async Task A_refresh_refused_while_SAP_is_still_working_on_the_last_one_is_not_tried_again_until_the_hold_ends()
    {
        var client = Client();
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        _clock.Advance(Fresh);
        var heldUntil = _clock.GetUtcNow() + TimeSpan.FromMinutes(7);
        _sap.ThrowNextList(new SapReadStillRunningException($"/b1s/v1/SQLQueries('WHS_BATCHES_{_warehouse}')/List", heldUntil));

        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        await _sap.WaitForListReadsAsync(2);
        await WaitUntilNoneInFlightAsync(client);

        _clock.Advance(TimeSpan.FromMinutes(7) - TimeSpan.FromSeconds(1));
        var held = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        Assert.True(held.IsStale);
        Assert.Equal(2, _sap.ListReads);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        await _sap.WaitForListReadsAsync(3);
    }

    [Fact]
    public async Task A_refresh_refused_because_the_read_family_is_full_waits_for_the_first_slot_to_free()
    {
        var client = Client();
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        _clock.Advance(Fresh);
        var frees = _clock.GetUtcNow() + TimeSpan.FromMinutes(4);
        _sap.ThrowNextList(new SapReadFamilyFullException("WHS_BATCHES", 2, "/b1s/v1/SQLQueries('WHS_BATCHES_X')/List", frees));

        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        await _sap.WaitForListReadsAsync(2);
        await WaitUntilNoneInFlightAsync(client);

        _clock.Advance(TimeSpan.FromMinutes(4) - TimeSpan.FromSeconds(1));
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        Assert.Equal(2, _sap.ListReads);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        await _sap.WaitForListReadsAsync(3);
    }

    // ── Too old to stand in ─────────────────────────────────────────────────

    [Fact]
    public async Task A_read_is_answered_stale_for_up_to_an_hour()
    {
        var client = Client();
        var first = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        _sap.FailEveryList(HttpStatusCode.NotFound);

        _clock.Advance(MaxStale);
        var page = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);

        Assert.True(page.IsStale);
        Assert.Equal(first.ReadAtUtc, page.ReadAtUtc);
    }

    [Fact]
    public async Task Past_an_hour_the_page_waits_for_SAP_and_fails_with_it()
    {
        var client = Client();
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        _sap.FailEveryList(HttpStatusCode.NotFound);

        _clock.Advance(MaxStale + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWarehouseBatchesForDisplayAsync(_warehouse));
    }

    [Fact]
    public async Task With_no_read_at_all_the_page_waits_for_SAP_and_fails_with_it()
    {
        _sap.FailEveryList(HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<HttpRequestException>(() => Client().GetWarehouseBatchesForDisplayAsync(_warehouse));
    }

    // ── A load landing ──────────────────────────────────────────────────────

    [Fact]
    public async Task After_a_load_lands_the_next_page_waits_for_the_refresh_and_shows_the_load()
    {
        var client = Client();
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        _sap.Stocked = ["CHE011", "NRI049", "YOG008"];

        _clock.Advance(TimeSpan.FromMinutes(1));
        SAPServiceLayerClient.MarkWarehouseBatchSnapshotDue(_cache, _warehouse, _clock.GetUtcNow());
        var page = await client.GetWarehouseBatchesForDisplayAsync(_warehouse).WaitAsync(Wait);

        Assert.False(page.IsStale);
        Assert.Contains("YOG008", page.StockedItemCodes);
        Assert.Equal(2, _sap.ListReads);
    }

    [Fact]
    public async Task After_a_load_lands_a_refresh_SAP_does_not_answer_leaves_the_read_from_before_it()
    {
        var client = Client();
        var first = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        var gate = _sap.HangNextList();
        SAPServiceLayerClient.MarkWarehouseBatchSnapshotDue(_cache, _warehouse, _clock.GetUtcNow());

        var pending = client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        await _sap.WaitForListReadsAsync(2);
        Assert.False(pending.IsCompleted);

        _clock.Advance(TimeSpan.FromSeconds(10));
        var page = await pending.WaitAsync(Wait);

        Assert.True(page.IsStale);
        Assert.Equal(first.ReadAtUtc, page.ReadAtUtc);
        gate.SetResult();
    }

    /// <summary>
    /// A refresh already on its way when the load landed answers with the figure from before it. The mark is
    /// a time, not a flag the refresh's answer could clear, so the page after still refreshes.
    /// </summary>
    [Fact]
    public async Task A_load_landing_while_a_refresh_is_in_flight_still_makes_the_next_page_refresh()
    {
        var client = Client();
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        var gate = _sap.HangNextList();
        _clock.Advance(Fresh);
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        await _sap.WaitForListReadsAsync(2);

        _clock.Advance(TimeSpan.FromSeconds(1));
        SAPServiceLayerClient.MarkWarehouseBatchSnapshotDue(_cache, _warehouse, _clock.GetUtcNow());
        gate.SetResult();
        await WaitUntilNoneInFlightAsync(client);

        await client.GetWarehouseBatchesForDisplayAsync(_warehouse).WaitAsync(Wait);

        Assert.Equal(3, _sap.ListReads);
    }

    [Fact]
    public async Task A_load_landing_elsewhere_changes_nothing_here()
    {
        var client = Client();
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
        SAPServiceLayerClient.MarkWarehouseBatchSnapshotDue(_cache, "VAN999", _clock.GetUtcNow());

        var page = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);

        Assert.False(page.IsStale);
        Assert.Equal(1, _sap.ListReads);
    }

    // ── What the page is handed ─────────────────────────────────────────────

    [Fact]
    public async Task The_stocked_codes_are_the_reads_own_distinct_and_in_cursor_order()
    {
        _sap.Stocked = ["pic003", "CHE011", "CHE011", " ", "NRI049"];

        var snapshot = await Client().GetWarehouseBatchesForDisplayAsync(_warehouse);

        Assert.Equal(["CHE011", "NRI049", "pic003"], snapshot.StockedItemCodes);
    }

    /// <summary>
    /// The per-item callers of the snapshot take their codes from the separate two-minute item-code read, so
    /// they must not be handed a batch read older than that.
    /// </summary>
    [Fact]
    public async Task The_per_item_snapshot_callers_still_read_again_after_two_minutes()
    {
        var client = Client();
        await client.GetBatchNumbersForItemsInWarehouseAsync(["CHE011"], _warehouse, allowCachedSnapshot: true);

        _clock.Advance(SAPServiceLayerClient.WarehouseBatchSnapshotLifetime - TimeSpan.FromSeconds(1));
        await client.GetBatchNumbersForItemsInWarehouseAsync(["CHE011"], _warehouse, allowCachedSnapshot: true);
        Assert.Equal(1, _sap.ListReads);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await client.GetBatchNumbersForItemsInWarehouseAsync(["CHE011"], _warehouse, allowCachedSnapshot: true);
        Assert.Equal(2, _sap.ListReads);
    }

    [Fact]
    public async Task The_fresh_window_and_the_hour_come_from_settings()
    {
        var client = Client(freshMinutes: 3, maxStaleMinutes: 5);
        await client.GetWarehouseBatchesForDisplayAsync(_warehouse);

        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True((await client.GetWarehouseBatchesForDisplayAsync(_warehouse)).IsStale);
        await _sap.WaitForListReadsAsync(2);
        await WaitForFreshAsync(client);

        _sap.FailEveryList(HttpStatusCode.NotFound);
        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWarehouseBatchesForDisplayAsync(_warehouse));
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private SAPServiceLayerClient Client(int freshMinutes = 10, int maxStaleMinutes = 60)
    {
        var httpClient = new HttpClient(_sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings
            {
                ServiceLayerUrl = "https://sap.invalid/b1s/v1/",
                Enabled = true,
                WarehouseBatchSnapshotFreshMinutes = freshMinutes,
                WarehouseBatchSnapshotMaxStaleMinutes = maxStaleMinutes,
                WarehouseBatchSnapshotLoadWaitSeconds = 10
            }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            _cache,
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            new StubItemUomMappingStore(),
            timeProvider: _clock);
    }

    /// <summary>Asks until a page comes back current: the background refresh has answered and been kept.</summary>
    private async Task<WarehouseBatchSnapshot> WaitForFreshAsync(SAPServiceLayerClient client)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var page = await client.GetWarehouseBatchesForDisplayAsync(_warehouse);
            if (!page.IsStale)
            {
                return page;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The background refresh never answered.");
    }

    /// <summary>Waits until the refresh SAP was sent has finished, answered or not, and its failure was noted.</summary>
    private async Task WaitUntilNoneInFlightAsync(SAPServiceLayerClient client)
    {
        _ = client;
        var deadline = DateTime.UtcNow + Wait;
        while (SAPServiceLayerClient.IsWarehouseBatchReadInFlight(_warehouse))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The refresh never finished.");
            }

            await Task.Delay(10);
        }

        // The failure is noted by a continuation that runs as the flight ends.
        await Task.Delay(50);
    }

    /// <summary>
    /// Serves one batch row per code in <see cref="Stocked"/> for every whole-warehouse read, and lets a test
    /// make the next one hang, fail or be refused.
    /// </summary>
    private sealed class ScriptedServiceLayer : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<ListRead> _reads = [];
        private Func<CancellationToken, Task<HttpResponseMessage>>? _next;
        private HttpStatusCode? _failEvery;

        public string[] Stocked { get; set; } = ["CHE011", "NRI049"];

        public int ListReads
        {
            get { lock (_sync) { return _reads.Count; } }
        }

        public IReadOnlyList<ListRead> Reads
        {
            get { lock (_sync) { return _reads.ToList(); } }
        }

        public TaskCompletionSource HangNextList()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) { _gates.Add(gate); }
            _next = async token =>
            {
                await gate.Task.WaitAsync(token);
                return Rows();
            };
            return gate;
        }

        public void FailNextList(HttpStatusCode status) => _next = _ => Task.FromResult(Json("{}", status));

        public void FailEveryList(HttpStatusCode status) => _failEvery = status;

        public void ThrowNextList(Exception error) => _next = _ => Task.FromException<HttpResponseMessage>(error);

        public void ReleaseAll()
        {
            lock (_sync)
            {
                foreach (var gate in _gates)
                {
                    gate.TrySetResult();
                }
            }
        }

        public async Task WaitForListReadsAsync(int count)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (ListReads < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"SAP saw {ListReads} batch reads, never {count}.");
                }

                await Task.Delay(10);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (path.EndsWith("/SQLQueries", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Json("{}", HttpStatusCode.Created);
            }

            if (path.EndsWith("/List", StringComparison.Ordinal))
            {
                lock (_sync)
                {
                    _reads.Add(new ListRead(SapRequestPriority.IsInteractive, SapRequestMarks.IsDisplayOnly(request)));
                }

                if (_failEvery is { } status)
                {
                    return Json("{}", status);
                }

                var next = Interlocked.Exchange(ref _next, null);
                return next is null ? Rows() : await next(cancellationToken);
            }

            // The existence probe: nothing is stored, so every statement is created once.
            return Json("{}", HttpStatusCode.NotFound);
        }

        private HttpResponseMessage Rows()
        {
            var rows = Stocked.Select(code => new
            {
                ItemCode = code,
                ItemName = code + " name",
                BatchNum = "B-" + code,
                InStock = 12.0m,
                WhsCode = "X"
            });

            return Json($"{{\"value\":{JsonSerializer.Serialize(rows)}}}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record ListRead(bool Interactive, bool DisplayOnly);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubItemUomMappingStore : ISapItemUomMappingStore
    {
        public Task<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>> GetAsync(
            IReadOnlyCollection<SapItemUomKey> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>>(
                new Dictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>());

        public Task SaveAsync(
            IReadOnlyCollection<(SapItemUomKey Key, string? UoMCode, int UoMEntry)> mappings,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
