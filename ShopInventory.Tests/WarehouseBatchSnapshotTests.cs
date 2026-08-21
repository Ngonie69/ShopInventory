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
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// How a warehouse's batch quantities are read for a catalogue page.
/// </summary>
/// <remarks>
/// The per-code path buckets item codes by three-character prefix and issues one stored query per
/// family. That is right when the caller has a handful of codes and needs them live, and wrong when
/// the caller is walking a whole catalogue a page at a time: a van's 465 codes span 39 families, so
/// paging it cost dozens of round trips that between them fetched the same warehouse over and over,
/// while a rep watched a spinner and gave up. These tests hold the two properties that fix depends
/// on — that the display path reads the warehouse once, and that the paths authorising a stock
/// movement still do not.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class WarehouseBatchSnapshotTests
{
    private const string Warehouse = "MSA";

    /// <summary>Codes drawn from three families, as a real page of this catalogue is.</summary>
    private static readonly string[] PageCodes = ["CHE011", "CHE042", "NRI049", "PIC003"];

    [Fact]
    public async Task A_snapshot_read_asks_the_warehouse_once_however_many_families_the_page_spans()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        await client.GetBatchNumbersForItemsInWarehouseAsync(
            PageCodes, Warehouse, allowCachedSnapshot: true);

        Assert.Single(sap.ExecutedBatchQueries);
        Assert.DoesNotContain(sap.CreatedStatements, statement => statement.Contains("LIKE", StringComparison.Ordinal));
    }

    /// <summary>
    /// The point of the change. Walking the catalogue is many calls over the same warehouse, and
    /// they have to cost one read between them, not one each.
    /// </summary>
    [Fact]
    public async Task Paging_a_catalogue_costs_one_warehouse_read_not_one_per_page()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        foreach (var page in new[] { new[] { "CHE011", "CHE042" }, ["NRI049"], new[] { "PIC003" } })
        {
            await client.GetBatchNumbersForItemsInWarehouseAsync(page, Warehouse, allowCachedSnapshot: true);
        }

        Assert.Single(sap.ExecutedBatchQueries);
    }

    /// <summary>
    /// Fewer reads must not mean a different answer: the snapshot covers the whole warehouse, so the
    /// surplus is filtered out here rather than left for the caller to trip over.
    /// </summary>
    [Fact]
    public async Task A_snapshot_read_returns_only_the_codes_that_were_asked_for()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        var batches = await client.GetBatchNumbersForItemsInWarehouseAsync(
            ["CHE011"], Warehouse, allowCachedSnapshot: true);

        Assert.Equal(["CHE011"], batches.Select(batch => batch.ItemCode).Distinct());
        Assert.All(batches, batch => Assert.Equal(Warehouse, batch.Warehouse));
    }

    /// <summary>
    /// A code the warehouse is not carrying comes back with nothing, not with someone else's batches.
    /// </summary>
    [Fact]
    public async Task A_code_the_warehouse_does_not_carry_comes_back_empty()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        Assert.Empty(await client.GetBatchNumbersForItemsInWarehouseAsync(
            ["ZZZ999"], Warehouse, allowCachedSnapshot: true));
    }

    /// <summary>
    /// The guard that keeps this a display-path optimisation. A validation or allocation read must
    /// still go to SAP for the codes it named, or a two-minute-old quantity authorises a line the
    /// warehouse cannot fill.
    /// </summary>
    [Fact]
    public async Task A_movement_path_still_reads_live_and_by_code()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        await client.GetBatchNumbersForItemsInWarehouseAsync(PageCodes, Warehouse);

        // One per family — CHE, NRI, PIC — and every one of them prefix-filtered.
        Assert.Equal(3, sap.ExecutedBatchQueries.Count);
        Assert.Equal(3, sap.CreatedStatements.Count(s => s.Contains("LIKE", StringComparison.Ordinal)));
    }

    /// <summary>A live read after a snapshot read is still a live read; the cache does not leak across.</summary>
    [Fact]
    public async Task A_snapshot_read_does_not_satisfy_a_later_live_read()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        await client.GetBatchNumbersForItemsInWarehouseAsync(["CHE011"], Warehouse, allowCachedSnapshot: true);
        sap.ExecutedBatchQueries.Clear();

        await client.GetBatchNumbersForItemsInWarehouseAsync(["CHE011"], Warehouse);

        Assert.Single(sap.ExecutedBatchQueries);
    }

    [Fact]
    public async Task Asking_about_no_codes_reads_nothing_at_all()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        Assert.Empty(await client.GetBatchNumbersForItemsInWarehouseAsync([], Warehouse, allowCachedSnapshot: true));
        Assert.Empty(sap.ExecutedBatchQueries);
    }

    // ── A missing query object is a fault, not an empty warehouse ───────────

    /// <summary>
    /// A query with no rows answers 200 with an empty array, and the paged read ends on the absence of
    /// a nextLink — so a 404 is never how "no batches" or "no more batches" arrives. It means the query
    /// object is gone. Reporting it as an empty warehouse is invisible downstream: the item falls back
    /// to a company-wide quantity that has nothing to do with this warehouse, and drops off the page.
    /// </summary>
    [Fact]
    public async Task A_missing_batch_query_is_an_error_rather_than_an_empty_warehouse()
    {
        var sap = new RecordingServiceLayer { BatchQueryMissing = true };
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetBatchNumbersForItemsInWarehouseAsync(PageCodes, Warehouse, allowCachedSnapshot: true));
    }

    [Fact]
    public async Task A_missing_batch_query_is_an_error_on_the_live_path_too()
    {
        var sap = new RecordingServiceLayer { BatchQueryMissing = true };
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetBatchNumbersForItemsInWarehouseAsync(PageCodes, Warehouse));
    }

    /// <summary>
    /// The subtler half. Losing the query partway through a paged read used to return the pages already
    /// in hand as though they were the whole warehouse — a truncation that understates stock for every
    /// item past the cut, and says nothing.
    /// </summary>
    [Fact]
    public async Task A_query_that_disappears_mid_read_never_returns_a_truncated_warehouse()
    {
        var sap = new RecordingServiceLayer { BatchQueryMissingAfterFirstPage = true };
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetBatchNumbersForItemsInWarehouseAsync(PageCodes, Warehouse, allowCachedSnapshot: true));
    }

    /// <summary>
    /// A failed read must not be remembered as an answer — otherwise one 404 empties the warehouse for
    /// as long as the snapshot is held.
    /// </summary>
    [Fact]
    public async Task A_failed_snapshot_read_is_not_cached()
    {
        var sap = new RecordingServiceLayer { BatchQueryMissing = true };
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetBatchNumbersForItemsInWarehouseAsync(["CHE011"], Warehouse, allowCachedSnapshot: true));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetBatchNumbersForItemsInWarehouseAsync(["CHE011"], Warehouse, allowCachedSnapshot: true));

        Assert.Equal(2, sap.ExecutedBatchQueries.Count);
    }

    private static SAPServiceLayerClient CreateClient(RecordingServiceLayer sap)
    {
        var httpClient = new HttpClient(sap)
        {
            BaseAddress = new Uri("https://sap.invalid/b1s/v1/")
        };

        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/", Enabled = true }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            new StubItemUomMappingStore());
    }

    /// <summary>
    /// Serves one batch row per stocked code, and records every stored-query execution so a test can
    /// count round trips. A prefix-filtered statement is answered with only the family it names, so
    /// the live path and the snapshot path return the same rows by different routes.
    /// </summary>
    private sealed class RecordingServiceLayer : HttpMessageHandler
    {
        private static readonly string[] Stocked = ["CHE011", "CHE042", "NRI049", "PIC003"];

        public List<string> CreatedCodes { get; } = [];

        public List<string> CreatedStatements { get; } = [];

        public List<string> ExecutedBatchQueries { get; } = [];

        /// <summary>SAP no longer holds the batch query object, from the very first read.</summary>
        public bool BatchQueryMissing { get; init; }

        /// <summary>
        /// The query object goes missing partway through a paged read: the first page comes back with
        /// rows and a nextLink, and the page it points at is gone.
        /// </summary>
        public bool BatchQueryMissingAfterFirstPage { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (path.EndsWith("/SQLQueries", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var created = JsonDocument.Parse(body).RootElement;
                CreatedCodes.Add(created.GetProperty("SqlCode").GetString()!);
                CreatedStatements.Add(created.GetProperty("SqlText").GetString()!);
                return Json("{}", HttpStatusCode.Created);
            }

            if (path.EndsWith("/List", StringComparison.Ordinal))
            {
                var queryCode = ExtractQueryCode(path);
                var isFirstPage = uri.Query.Contains("$skip=0", StringComparison.Ordinal) ||
                                  !uri.Query.Contains("$skip=", StringComparison.Ordinal);

                if (BatchQueryMissing)
                {
                    ExecutedBatchQueries.Add(queryCode);
                    return Json("{}", HttpStatusCode.NotFound);
                }

                if (isFirstPage)
                {
                    ExecutedBatchQueries.Add(queryCode);

                    var rows = Rows(StatementFor(queryCode));

                    return BatchQueryMissingAfterFirstPage
                        ? Json($"{{\"value\":{rows},\"odata.nextLink\":\"SQLQueries('{queryCode}')/List?$skip=500\"}}")
                        : Json($"{{\"value\":{rows}}}");
                }

                if (BatchQueryMissingAfterFirstPage)
                {
                    return Json("{}", HttpStatusCode.NotFound);
                }

                return Json("{\"value\":[]}");
            }

            // The existence probe. Nothing is stored, so every statement is created once.
            return Json("{}", HttpStatusCode.NotFound);
        }

        /// <summary>The statement stored under a code, so a List read can honour its prefix filter.</summary>
        private string StatementFor(string queryCode)
        {
            var index = CreatedCodes.LastIndexOf(queryCode);
            return index < 0 ? string.Empty : CreatedStatements[index];
        }

        /// <summary>One row per stocked code the statement selects — all of them, or one family.</summary>
        private static string Rows(string statement)
        {
            var codes = Stocked.AsEnumerable();

            var marker = statement.IndexOf("LIKE '", StringComparison.Ordinal);
            if (marker >= 0)
            {
                var prefix = statement[(marker + 6)..];
                prefix = prefix[..prefix.IndexOf('%')];
                codes = codes.Where(code => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }

            return "[" + string.Join(",", codes.Select(code =>
                $$"""{"ItemCode":"{{code}}","ItemName":"{{code}} name","BatchNum":"B-{{code}}","InStock":12.0,"WhsCode":"MSA"}""")) + "]";
        }

        private static string ExtractQueryCode(string path)
        {
            var open = path.IndexOf('\'');
            var close = path.LastIndexOf('\'');
            return open >= 0 && close > open ? path[(open + 1)..close] : path;
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

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
