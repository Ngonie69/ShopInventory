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
/// The two SQL paths whose SqlCode used to carry request data: the warehouse sales report, keyed by
/// warehouse and date range, and the batch search, keyed by whatever a user typed.
/// </summary>
/// <remarks>
/// A SAP <c>SQLQueries</c> object cannot practically be deleted — DELETE returns 502 at 300s and the
/// row survives — so a code that varies per request is a permanent leak, not a cache miss. These
/// assert on the number of objects created rather than on the SQL text, because that is the property
/// that actually matters and the one a future edit is most likely to break silently.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapBoundQueryLeakTests
{
    [Fact]
    public async Task Every_warehouse_and_date_range_shares_one_sales_query_object()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        await client.GetSalesQuantitiesByWarehouseAsync("WH01", new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
        await client.GetSalesQuantitiesByWarehouseAsync("WH01", new DateTime(2026, 2, 1), new DateTime(2026, 2, 28));
        await client.GetSalesQuantitiesByWarehouseAsync("WH02", new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
        await client.GetSalesQuantitiesByWarehouseAsync("WH02", new DateTime(2026, 3, 17), new DateTime(2026, 3, 18));

        Assert.Equal(["SALES_QTY"], sap.CreatedCodes);
        Assert.Equal(4, sap.ExecutedQueries.Count);
    }

    [Fact]
    public async Task The_sales_range_reaches_sap_as_bound_values()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        await client.GetSalesQuantitiesByWarehouseAsync("WH01", new DateTime(2026, 3, 17), new DateTime(2026, 4, 2));

        var executed = Assert.Single(sap.ExecutedQueries);
        Assert.Contains("whsCode='WH01'", executed, StringComparison.Ordinal);
        // 'yyyy-MM-dd'. The yyyyMMdd SAP returns matches nothing on the way in, silently.
        Assert.Contains("fromDate='2026-03-17'", executed, StringComparison.Ordinal);
        Assert.Contains("toDate='2026-04-02'", executed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The statement dropped <c>ORDER BY SUM(...)</c> — SAP's validator rejects ordering by an
    /// aggregate — so the ranking callers render has to survive on the read side instead.
    /// </summary>
    [Fact]
    public async Task Sales_rows_come_back_ranked_by_quantity()
    {
        var sap = new RecordingServiceLayer
        {
            Rows = """
                [
                  {"ItemCode":"AAA","TotalQuantitySold":4,"TotalSalesValue":40,"InvoiceCount":1},
                  {"ItemCode":"BBB","TotalQuantitySold":91,"TotalSalesValue":910,"InvoiceCount":3},
                  {"ItemCode":"CCC","TotalQuantitySold":17,"TotalSalesValue":170,"InvoiceCount":2}
                ]
                """
        };
        var client = CreateClient(sap);

        var sales = await client.GetSalesQuantitiesByWarehouseAsync(
            "WH01",
            new DateTime(2026, 1, 1),
            new DateTime(2026, 1, 31));

        Assert.Equal(["BBB", "CCC", "AAA"], sales.Select(sale => sale.ItemCode));
    }

    [Fact]
    public async Task Every_search_term_shares_one_batch_search_query_object()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        await client.SearchBatchesByBatchNumberAsync("B2401");
        await client.SearchBatchesByBatchNumberAsync("B2402");
        await client.SearchBatchesByBatchNumberAsync("something else entirely");

        Assert.Equal(["BATCH_SEARCH"], sap.CreatedCodes);
        Assert.Equal(3, sap.ExecutedQueries.Count);
    }

    /// <summary>
    /// The case fold moved from the column to the bound value — SAP rejects a parameter compared
    /// against <c>UPPER(col)</c> — so the term goes over in all three cases the search matches on.
    /// </summary>
    [Theory]
    [InlineData("b2401", "%B2401%", "%b2401%", "%b2401%")]
    [InlineData("  B2401  ", "%B2401%", "%b2401%", "%B2401%")]
    // A mixed-case batch number is only found when typed as stored, which is what exactTerm is for.
    [InlineData("Ab12", "%AB12%", "%ab12%", "%Ab12%")]
    // The SQL literal this replaced took the term verbatim, so a quote in it has to stay harmless.
    [InlineData("O'Brien", "%O''BRIEN%", "%o''brien%", "%O''Brien%")]
    public async Task The_search_term_reaches_sap_wildcarded_and_bound(
        string typed,
        string expectedUpper,
        string expectedLower,
        string expectedExact)
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        await client.SearchBatchesByBatchNumberAsync(typed);

        var executed = Assert.Single(sap.ExecutedQueries);
        Assert.Contains($"upperTerm='{expectedUpper}'", executed, StringComparison.Ordinal);
        Assert.Contains($"lowerTerm='{expectedLower}'", executed, StringComparison.Ordinal);
        Assert.Contains($"exactTerm='{expectedExact}'", executed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Padding is what lets the slot count stay off the statement. A three-item batch and a full one
    /// have to produce the same text, or the leak comes back keyed on line count instead.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(SAPServiceLayerClient.UomHistorySlotCount)]
    public void A_short_uom_batch_binds_the_full_slot_count(int batchSize)
    {
        var batch = Enumerable.Range(1, batchSize).Select(index => $"ITEM{index:D3}").ToList();

        var parameters = SAPServiceLayerClient.BuildSalesOrderLineUomHistoryParameters(batch);

        Assert.Equal(SAPServiceLayerClient.UomHistorySlotCount, parameters.Count);
        Assert.Equal("ITEM001", parameters["i01"]);
        Assert.Equal(batch[^1], parameters[$"i{batchSize:D2}"]);

        // Surplus slots bind to a value no item code can equal, so they match nothing rather than
        // widening the result.
        for (var slot = batchSize + 1; slot <= SAPServiceLayerClient.UomHistorySlotCount; slot++)
        {
            Assert.Equal(string.Empty, parameters[$"i{slot:D2}"]);
        }
    }

    [Fact]
    public async Task A_blank_search_term_asks_sap_nothing()
    {
        var sap = new RecordingServiceLayer();
        var client = CreateClient(sap);

        Assert.Empty(await client.SearchBatchesByBatchNumberAsync("   "));

        Assert.Empty(sap.CreatedCodes);
        Assert.Empty(sap.ExecutedQueries);
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
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            new StubItemUomMappingStore());
    }

    /// <summary>
    /// Records which query objects were created and the parameters each execution carried. Answers
    /// 404 to the existence probe so every statement is created rather than assumed present.
    /// </summary>
    private sealed class RecordingServiceLayer : HttpMessageHandler
    {
        /// <summary>Rows the execute returns, as the JSON array SAP puts in <c>value</c>.</summary>
        public string Rows { get; init; } = "[]";

        public List<string> CreatedCodes { get; } = [];

        /// <summary>One entry per execute, unescaped so an assertion can read the bound values.</summary>
        public List<string> ExecutedQueries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (path.EndsWith("/SQLQueries", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                CreatedCodes.Add(JsonDocument.Parse(body).RootElement.GetProperty("SqlCode").GetString()!);
                return Json("{}", HttpStatusCode.Created);
            }

            if (path.EndsWith("/List", StringComparison.Ordinal))
            {
                ExecutedQueries.Add(Uri.UnescapeDataString(request.RequestUri.Query));
                return Json($"{{\"value\":{Rows}}}");
            }

            // The existence probe. Nothing is stored, so everything is created once.
            return Json("{}", HttpStatusCode.NotFound);
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
