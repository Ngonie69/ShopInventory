using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A warehouse's transfer requests and transfers are read over a date window and paged to the end.
/// The request list used to send no page size and so stopped at SAP's default 20 rows; the transfer
/// history used to read every transfer the warehouse had ever made.
/// </summary>
[Collection("SapServiceLayerClient")]
public class WarehouseTransferReadWindowTests
{
    [Fact]
    public async Task A_shop_with_more_than_twenty_requests_gets_all_of_them_not_the_first_page()
    {
        var sap = new PagingServiceLayer(rows: 45);
        var client = CreateClient(sap);

        var requests = await client.GetInventoryTransferRequestsByWarehouseAsync("SHOP01", new DateTime(2026, 7, 1));

        Assert.Equal(45, requests.Count);
        Assert.Equal(45, requests.Select(request => request.DocEntry).Distinct().Count());
        Assert.All(sap.Lists, list => Assert.True(list.AskedForPageSize, $"No page size on {list.Url}"));
    }

    [Fact]
    public async Task The_request_list_keeps_older_open_requests_beside_the_window()
    {
        var sap = new PagingServiceLayer(rows: 3);
        var client = CreateClient(sap);

        await client.GetInventoryTransferRequestsByWarehouseAsync("SHOP01", new DateTime(2026, 7, 1));

        var filter = Assert.Single(sap.Lists).Filter;
        Assert.Contains("ToWarehouse eq 'SHOP01'", filter);
        Assert.Contains("(DocDate ge '2026-07-01' or DocumentStatus eq 'bost_Open')", filter);
    }

    [Fact]
    public async Task The_transfer_history_is_bounded_by_both_dates_and_paged_to_the_end()
    {
        var sap = new PagingServiceLayer(rows: 130);
        var client = CreateClient(sap);

        var transfers = await client.GetInventoryTransfersToWarehouseAsync(
            "SHOP01", new DateTime(2026, 7, 1), new DateTime(2026, 9, 29));

        Assert.Equal(130, transfers.Count);
        Assert.All(sap.Lists, list =>
        {
            Assert.Contains("(ToWarehouse eq 'SHOP01' or FromWarehouse eq 'SHOP01')", list.Filter);
            Assert.Contains("DocDate ge '2026-07-01' and DocDate le '2026-09-29'", list.Filter);
            Assert.True(list.AskedForPageSize);
        });
    }

    [Fact]
    public void With_no_dates_the_window_is_the_last_ninety_days()
    {
        var today = new DateTime(2026, 9, 29, 14, 30, 0);

        Assert.Equal((new DateTime(2026, 7, 1), new DateTime(2026, 9, 29)), TransferReadWindow.Resolve(null, null, today));
        Assert.Equal((new DateTime(2026, 1, 1), new DateTime(2026, 9, 29)), TransferReadWindow.Resolve(new DateTime(2026, 1, 1), null, today));
        Assert.Equal((new DateTime(2026, 3, 3), new DateTime(2026, 6, 1)), TransferReadWindow.Resolve(null, new DateTime(2026, 6, 1), today));
        Assert.Equal((new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)), TransferReadWindow.Resolve(new DateTime(2026, 2, 1), new DateTime(2026, 1, 1), today));
    }

    private static SAPServiceLayerClient CreateClient(PagingServiceLayer sap)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
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
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    /// <summary>
    /// Holds <c>rows</c> documents and honours <c>$top</c>, <c>$skip</c> and the page-size header the
    /// way SAP does: without the header, a page is 20 rows whatever <c>$top</c> says.
    /// </summary>
    private sealed class PagingServiceLayer(int rows) : HttpMessageHandler
    {
        public List<(string Url, string Filter, bool AskedForPageSize)> Lists { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));
            }

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var askedForPageSize = request.Headers.TryGetValues("Prefer", out var prefer) &&
                                   prefer.Any(value => value.Contains("odata.maxpagesize", StringComparison.Ordinal));
            Lists.Add((uri.ToString(), query["$filter"] ?? string.Empty, askedForPageSize));

            var skip = int.TryParse(query["$skip"], out var s) ? s : 0;
            var top = int.TryParse(query["$top"], out var t) ? t : 20;
            var pageSize = askedForPageSize ? top : Math.Min(top, 20);
            var page = Enumerable.Range(skip, Math.Max(0, Math.Min(pageSize, rows - skip)))
                .Select(index => $$"""{"DocEntry":{{rows - index}},"DocNum":{{rows - index}},"ToWarehouse":"SHOP01","FromWarehouse":"MAIN","DocumentStatus":"bost_Open","StockTransferLines":[]}""");
            return Task.FromResult(Json($"{{\"value\":[{string.Join(",", page)}]}}"));
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
