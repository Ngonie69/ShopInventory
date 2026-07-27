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
/// Covers the "by customer" / "by supplier" document lists.
/// </summary>
/// <remarks>
/// These issued a single request with no <c>$top</c>, no <c>Prefer: odata.maxpagesize</c> and no
/// paging loop, so they returned whatever the Service Layer's default page happened to be — around
/// 20 rows — while their names and return types promised the lot. That is a silent wrong answer
/// rather than a slow one: a supplier with 60 purchase orders showed 20, with nothing to say the
/// rest existed.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapDocumentListPagingTests
{
    [Fact]
    public async Task All_pages_are_read_not_just_the_first()
    {
        var sap = new PagedDocuments(totalRows: 1250);
        var client = CreateClient(sap);

        var orders = await client.GetPurchaseOrdersBySupplierAsync("V0001");

        Assert.Equal(1250, orders.Count);
        Assert.Equal(3, sap.RequestCount);
    }

    [Fact]
    public async Task A_short_page_ends_the_walk()
    {
        var sap = new PagedDocuments(totalRows: 12);
        var client = CreateClient(sap);

        var orders = await client.GetPurchaseOrdersBySupplierAsync("V0001");

        Assert.Equal(12, orders.Count);
        Assert.Equal(1, sap.RequestCount);
    }

    [Fact]
    public async Task An_empty_result_costs_one_request()
    {
        var sap = new PagedDocuments(totalRows: 0);
        var client = CreateClient(sap);

        var orders = await client.GetPurchaseOrdersBySupplierAsync("V0001");

        Assert.Empty(orders);
        Assert.Equal(1, sap.RequestCount);
    }

    [Fact]
    public async Task An_unbounded_history_stops_at_the_ceiling()
    {
        // No date bound on these lists, so a long-standing trading partner would otherwise be walked
        // in full. The cap is what stops one request doing that.
        var sap = new PagedDocuments(totalRows: 100_000);
        var client = CreateClient(sap);

        var orders = await client.GetPurchaseOrdersBySupplierAsync("V0001");

        Assert.Equal(2000, orders.Count);
        Assert.Equal(4, sap.RequestCount);
    }

    [Fact]
    public async Task Every_page_asks_for_an_explicit_size()
    {
        var sap = new PagedDocuments(totalRows: 1250);
        var client = CreateClient(sap);

        await client.GetPurchaseOrdersBySupplierAsync("V0001");

        Assert.All(sap.Urls, url => Assert.Contains("$top=", url, StringComparison.Ordinal));
        Assert.Equal(["$skip=0", "$skip=500", "$skip=1000"], sap.Urls.Select(SkipOf));
    }

    private static string SkipOf(string url) =>
        url[url.IndexOf("$skip=", StringComparison.Ordinal)..];

    private static SAPServiceLayerClient CreateClient(PagedDocuments sap)
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
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    /// <summary>Serves a document list of a fixed size, honouring $top and $skip the way SL does.</summary>
    private sealed class PagedDocuments(int totalRows) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));
            }

            RequestCount++;
            Urls.Add(target);

            var top = IntParam(target, "$top") ?? 20;
            var skip = IntParam(target, "$skip") ?? 0;
            var count = Math.Clamp(totalRows - skip, 0, top);

            var rows = Enumerable.Range(skip, count)
                .Select(docEntry => new { DocEntry = docEntry, DocNum = docEntry });

            return Task.FromResult(Json(JsonSerializer.Serialize(new { value = rows })));
        }

        private static int? IntParam(string url, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                url, System.Text.RegularExpressions.Regex.Escape(name) + @"=(\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
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
}
