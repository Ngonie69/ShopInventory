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
///
/// The first fix asked SAP for every transfer in the range and matched lines here, which on
/// 2026-10-15 took 117 seconds for KEFSHOP's month and timed out the till. Now the header read does
/// the bulk, a <c>$crossjoin</c> on the lines names the rest, and those are read by key.
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
    public async Task SAP_is_never_asked_for_every_transfer_in_the_range()
    {
        var sap = new StockTransfers(Documents);

        await CreateClient(sap).GetInventoryTransfersTouchingWarehouseAsync(
            Shop, new DateTime(2026, 9, 12), new DateTime(2026, 9, 13));

        var reads = sap.Urls.Select(Uri.UnescapeDataString).ToList();
        Assert.Contains(reads, url => url.Contains(
            "$filter=(ToWarehouse eq 'KEFSHOP' or FromWarehouse eq 'KEFSHOP') and DocDate ge '2026-09-12' and DocDate le '2026-09-13'&",
            StringComparison.Ordinal));
        Assert.Contains(reads, url => url.Contains("$crossjoin(StockTransfers,StockTransfers/StockTransferLines)", StringComparison.Ordinal)
            && url.Contains("StockTransferLines/FromWarehouseCode eq 'KEFSHOP'", StringComparison.Ordinal));
        Assert.DoesNotContain(reads, url => url.Contains("$filter=DocDate ge", StringComparison.Ordinal));
        Assert.DoesNotContain(reads, url => url.Contains("/any(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_the_documents_the_header_read_missed_are_read_by_key()
    {
        var sap = new StockTransfers(Documents);

        await CreateClient(sap).GetInventoryTransfersTouchingWarehouseAsync(
            Shop, new DateTime(2026, 9, 12), new DateTime(2026, 9, 13));

        var byKey = Assert.Single(
            sap.Urls.Select(Uri.UnescapeDataString),
            url => url.Contains("$filter=DocEntry eq", StringComparison.Ordinal));
        Assert.Contains("$filter=DocEntry eq 3 or DocEntry eq 4&", byKey, StringComparison.Ordinal);
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
    public async Task Every_page_of_the_header_read_is_read()
    {
        var many = Enumerable.Range(1, 1100)
            .Select(entry => Transfer(entry, "KEFHQ", Shop, Line("Y", from: null, to: null)))
            .ToArray();
        var sap = new StockTransfers(many);

        var transfers = await CreateClient(sap).GetInventoryTransfersTouchingWarehouseAsync(
            Shop, new DateTime(2026, 9, 1), new DateTime(2026, 9, 13));

        Assert.Equal(1100, transfers.Count);
        Assert.Equal(1100, transfers[0].DocEntry);
    }

    [Fact]
    public async Task Every_page_of_the_line_read_is_read_and_its_documents_fetched()
    {
        // 250 documents headed elsewhere, each with two lines leaving the shop: 500 crossjoin rows.
        var many = Enumerable.Range(1, 250)
            .Select(entry => Transfer(entry, "KEFHQ", "KEFGRS",
                Line("A", from: Shop, to: "KEFGRS"), Line("B", from: Shop, to: "KEFGRS")))
            .ToArray();
        var sap = new StockTransfers(many);

        var transfers = await CreateClient(sap).GetInventoryTransfersTouchingWarehouseAsync(
            Shop, new DateTime(2026, 9, 1), new DateTime(2026, 9, 13));

        Assert.Equal(Enumerable.Range(1, 250).Reverse(), transfers.Select(t => t.DocEntry));
        Assert.True(sap.Urls.Count(url => url.Contains("crossjoin", StringComparison.Ordinal)) > 1);
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

    /// <summary>
    /// Serves a fixed set of stock transfers to the three reads the client makes — by header
    /// warehouse, by line warehouse through <c>$crossjoin</c>, and by key — honouring $top and $skip
    /// as SL does. Any other filter is answered with every document, as SAP would answer a date-only
    /// filter over this set.
    /// </summary>
    private sealed class StockTransfers(object[] documents) : HttpMessageHandler
    {
        private readonly JsonElement[] _documents = documents
            .Select(document => JsonSerializer.SerializeToElement(document))
            .ToArray();

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

            var url = Uri.UnescapeDataString(target);
            var top = IntParam(url, "$top") ?? 20;
            var skip = IntParam(url, "$skip") ?? 0;

            List<object> rows;
            if (url.Contains("$crossjoin(", StringComparison.Ordinal))
            {
                var warehouse = Match(url, @"StockTransferLines/WarehouseCode eq '([^']+)'");
                rows = _documents
                    .SelectMany(document => document.GetProperty("StockTransferLines").EnumerateArray()
                        .Where(line => Text(line, "WarehouseCode") == warehouse || Text(line, "FromWarehouseCode") == warehouse)
                        .Select(_ => (object)new { StockTransfers = new { DocEntry = DocEntry(document) } }))
                    .ToList();
            }
            else if (Match(url, @"\(ToWarehouse eq '([^']+)'") is { } header)
            {
                rows = DocumentsWhere(document => Text(document, "ToWarehouse") == header || Text(document, "FromWarehouse") == header);
            }
            else if (url.Contains("$filter=DocEntry eq", StringComparison.Ordinal))
            {
                var keys = System.Text.RegularExpressions.Regex.Matches(url, @"DocEntry eq (\d+)")
                    .Select(match => int.Parse(match.Groups[1].Value))
                    .ToHashSet();
                rows = DocumentsWhere(document => keys.Contains(DocEntry(document)));
            }
            else
            {
                rows = DocumentsWhere(_ => true);
            }

            var page = rows.Skip(skip).Take(top);
            return Task.FromResult(Json(JsonSerializer.Serialize(new { value = page })));
        }

        // In DocEntry order, descending, as the client asks for its document reads.
        private List<object> DocumentsWhere(Func<JsonElement, bool> predicate) => _documents
            .Where(predicate)
            .OrderByDescending(DocEntry)
            .Cast<object>()
            .ToList();

        private static int DocEntry(JsonElement document) => document.GetProperty("DocEntry").GetInt32();

        private static string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string? Match(string url, string pattern)
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, pattern);
            return match.Success ? match.Groups[1].Value : null;
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
