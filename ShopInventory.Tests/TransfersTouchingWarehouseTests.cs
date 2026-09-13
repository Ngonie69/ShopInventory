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
/// A shop's transfer list includes every transfer that moves its stock, whether the shop is named on
/// the document's header or only on one of its lines.
/// </summary>
/// <remarks>
/// The list asked SAP for <c>ToWarehouse eq shop or FromWarehouse eq shop</c>, which reads the header
/// only. SAP lets each line carry its own warehouses, and TransferEventListener moves a shop's stock
/// ledger by those line warehouses — so a document headed between two other warehouses could change
/// the shop's stock and still never appear in the shop's list.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class TransfersTouchingWarehouseTests
{
    private const string Shop = "KEFSHOP";

    private static readonly object[] Documents =
    [
        // Headed into the shop.
        Transfer(1, "KEFHQ", Shop, Line("A", from: null, to: null)),
        // Headed out of the shop.
        Transfer(2, Shop, "KEFGRS", Line("B", from: null, to: null)),
        // Headed between two other warehouses; one line leaves from the shop.
        Transfer(3, "KEFHQ", "KEFGRS", Line("C", from: "KEFHQ", to: "KEFGRS"), Line("D", from: Shop, to: "KEFGRS")),
        // Headed between two other warehouses; one line arrives in the shop.
        Transfer(4, "KEFHQ", "KEFBYC", Line("E", from: null, to: Shop)),
        // Nothing to do with the shop.
        Transfer(5, "KEFHQ", "KEFGRS", Line("F", from: null, to: null)),
    ];

    [Fact]
    public async Task A_transfer_whose_line_touches_the_shop_is_included()
    {
        var sap = new StockTransfers(Documents);

        var transfers = await CreateClient(sap).GetInventoryTransfersTouchingWarehouseAsync(
            Shop, new DateTime(2026, 9, 12), new DateTime(2026, 9, 13));

        Assert.Equal([1, 2, 3, 4], transfers.Select(t => t.DocEntry).Order());
    }

    [Fact]
    public async Task SAP_is_asked_by_date_alone_because_it_refuses_filters_on_lines()
    {
        var sap = new StockTransfers(Documents);

        await CreateClient(sap).GetInventoryTransfersTouchingWarehouseAsync(
            Shop, new DateTime(2026, 9, 12), new DateTime(2026, 9, 13));

        var url = Uri.UnescapeDataString(Assert.Single(sap.Urls));
        Assert.Contains("$filter=DocDate ge '2026-09-12' and DocDate le '2026-09-13'&", url, StringComparison.Ordinal);
        Assert.DoesNotContain("Warehouse eq", url, StringComparison.Ordinal);
        Assert.DoesNotContain("/any(", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_header_read_is_unchanged_for_its_other_callers()
    {
        var sap = new StockTransfers(Documents);

        await CreateClient(sap).GetInventoryTransfersByDateRangeAsync(
            Shop, new DateTime(2026, 9, 12), new DateTime(2026, 9, 13));

        var url = Uri.UnescapeDataString(Assert.Single(sap.Urls));
        Assert.Contains(
            "$filter=(ToWarehouse eq 'KEFSHOP' or FromWarehouse eq 'KEFSHOP') and DocDate ge '2026-09-12' and DocDate le '2026-09-13'&",
            url,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_page_is_read_before_matching()
    {
        // 1,100 documents with the shop only on the last one's line.
        var many = Enumerable.Range(1, 1100)
            .Select(entry => entry == 1100
                ? Transfer(entry, "KEFHQ", "KEFGRS", Line("Z", from: Shop, to: "KEFGRS"))
                : Transfer(entry, "KEFHQ", "KEFGRS", Line("Y", from: null, to: null)))
            .ToArray();
        var sap = new StockTransfers(many);

        var transfers = await CreateClient(sap).GetInventoryTransfersTouchingWarehouseAsync(
            Shop, new DateTime(2026, 9, 1), new DateTime(2026, 9, 13));

        Assert.Equal(1100, Assert.Single(transfers).DocEntry);
        Assert.Equal(3, sap.Urls.Count);
    }

    private static object Transfer(int docEntry, string from, string to, params object[] lines) => new
    {
        DocEntry = docEntry,
        DocNum = 88000 + docEntry,
        DocDate = "2026-09-12",
        FromWarehouse = from,
        ToWarehouse = to,
        StockTransferLines = lines,
    };

    private static object Line(string item, string? from, string? to) => new
    {
        LineNum = 0,
        ItemCode = item,
        Quantity = 5m,
        FromWarehouseCode = from,
        WarehouseCode = to,
    };

    private static SAPServiceLayerClient CreateClient(StockTransfers sap)
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

    /// <summary>Serves a fixed set of stock transfers, honouring $top and $skip as SL does.</summary>
    private sealed class StockTransfers(object[] documents) : HttpMessageHandler
    {
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

            Urls.Add(target);

            var top = IntParam(target, "$top") ?? 20;
            var skip = IntParam(target, "$skip") ?? 0;
            var page = documents.Skip(skip).Take(top);

            return Task.FromResult(Json(JsonSerializer.Serialize(new { value = page })));
        }

        private static int? IntParam(string url, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                Uri.UnescapeDataString(url), System.Text.RegularExpressions.Regex.Escape(name) + @"=(\d+)");
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
