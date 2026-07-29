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
/// Covers the batched lookups that replaced per-customer and per-order fan-out.
/// </summary>
/// <remarks>
/// Three call sites looped over a collection issuing one SAP walk per element: the account sales
/// and payment report over its accounts, the statement aging summary over its card codes, and the
/// sales order reconciliation sweep over its candidates. The last was the worst, because each probe
/// is a scan of ORDR on an unindexed UDF.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapCustomerFanOutTests
{
    [Fact]
    public async Task Invoices_for_many_customers_are_one_filter_not_one_walk_each()
    {
        var sap = new QueryRecorder();
        var client = CreateClient(sap);

        await client.GetInvoicesByCustomersAsync(
            ["C1", "C2", "C3", "C4"],
            new DateTime(2026, 1, 1),
            new DateTime(2026, 6, 30));

        var request = Assert.Single(sap.Urls);
        var filter = Uri.UnescapeDataString(request);
        Assert.Contains("CardCode eq 'C1' or CardCode eq 'C2'", filter, StringComparison.Ordinal);
        Assert.Contains("DocDate ge '2026-01-01'", filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customer_lists_are_chunked_so_the_filter_stays_a_sane_length()
    {
        var sap = new QueryRecorder();
        var client = CreateClient(sap);

        await client.GetInvoicesByCustomersAsync(
            [.. Enumerable.Range(1, 60).Select(index => $"C{index:D4}")],
            new DateTime(2026, 1, 1),
            new DateTime(2026, 6, 30));

        // 60 codes at 25 per chunk, and the whole filter travels in the URL.
        Assert.Equal(3, sap.Urls.Count);
    }

    [Fact]
    public async Task Open_invoice_lookup_asks_sap_to_do_the_filtering()
    {
        // Aging used to read every invoice a customer ever had and keep the few still open.
        var sap = new QueryRecorder();
        var client = CreateClient(sap);

        await client.GetOpenInvoicesByCustomersAsync(["C1"]);

        var filter = Uri.UnescapeDataString(Assert.Single(sap.Urls));
        Assert.Contains("DocumentStatus eq 'bost_Open'", filter, StringComparison.Ordinal);
        Assert.Contains("Cancelled eq 'tNO'", filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_customers_means_no_request_at_all()
    {
        var sap = new QueryRecorder();
        var client = CreateClient(sap);

        Assert.Empty(await client.GetInvoicesByCustomersAsync([], DateTime.UtcNow, DateTime.UtcNow));
        Assert.Empty(sap.Urls);
    }

    [Fact]
    public async Task Order_numbers_are_resolved_in_one_scan_and_keyed_back()
    {
        var sap = new QueryRecorder
        {
            OrdersByNumber = { ["ORD-1"] = 500, ["ORD-3"] = 700 }
        };
        var client = CreateClient(sap);

        var resolved = await client.GetSalesOrdersByOrderNumbersAsync(["ORD-1", "ORD-2", "ORD-3"]);

        Assert.Single(sap.Urls);
        Assert.Equal(500, resolved["ORD-1"].DocEntry);
        Assert.Equal(700, resolved["ORD-3"].DocEntry);
        // An order SAP does not hold is simply absent, not a null entry.
        Assert.False(resolved.ContainsKey("ORD-2"));
    }

    [Fact]
    public async Task Duplicate_order_numbers_resolve_to_the_highest_doc_entry()
    {
        // U_OrderNumber is meant to be unique; more than one match means SAP already holds
        // duplicates, and the single-order probe picks the newest. This has to agree with it.
        var sap = new QueryRecorder { DuplicateFor = "ORD-1" };
        var client = CreateClient(sap);

        var resolved = await client.GetSalesOrdersByOrderNumbersAsync(["ORD-1"]);

        Assert.Equal(900, resolved["ORD-1"].DocEntry);
    }

    private static SAPServiceLayerClient CreateClient(QueryRecorder sap)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
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

    private sealed class QueryRecorder : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        public Dictionary<string, int> OrdersByNumber { get; } = [];

        public string? DuplicateFor { get; set; }

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

            if (target.Contains("/Orders?", StringComparison.Ordinal))
            {
                var rows = OrdersByNumber
                    .Select(entry => new { DocEntry = entry.Value, DocNum = entry.Value, U_OrderNumber = entry.Key })
                    .ToList();

                if (DuplicateFor is not null)
                {
                    rows =
                    [
                        new { DocEntry = 400, DocNum = 400, U_OrderNumber = DuplicateFor },
                        new { DocEntry = 900, DocNum = 900, U_OrderNumber = DuplicateFor },
                        new { DocEntry = 650, DocNum = 650, U_OrderNumber = DuplicateFor }
                    ];
                }

                return Task.FromResult(Json(JsonSerializer.Serialize(new { value = rows })));
            }

            return Task.FromResult(Json("{\"value\":[]}"));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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
