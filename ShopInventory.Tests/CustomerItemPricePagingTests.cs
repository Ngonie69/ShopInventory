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
/// Covers the targeted customer price lookup that fed sales order 80151.
/// </summary>
/// <remarks>
/// The lookup asked for eight items on price list 11 and got seven back. Item entities are large —
/// YOG101 alone carries about a hundred ItemPrices rows — so the Service Layer can cut a page short
/// and still hand back a continuation link. The loop then advanced <c>skip</c> by the page size it
/// had asked for rather than the rows it received, stepped over the remainder, and returned an item
/// short with no error. That item's order line went to SAP at 0.00.
///
/// Nothing in the response says an item is missing, which is why these tests assert on the request
/// sequence as much as the result.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class CustomerItemPricePagingTests
{
    private static readonly string[] OrderItems =
        ["YOG127", "YOG100", "YOG101", "YOG102", "YOG126", "DAI008", "DAI009", "DAI010"];

    [Fact]
    public async Task A_short_page_does_not_lose_the_items_behind_it()
    {
        // SAP returns seven of the eight and a continuation link, exactly as it did for 80151.
        var sap = new PagedItemPrices(pricedItems: OrderItems, firstPageRows: 7);
        var client = CreateClient(sap);

        var prices = await client.GetItemPricesForCustomerAsync("NRI049", OrderItems);

        Assert.Equal(8, prices.Count);
        Assert.Contains(prices, price => price.ItemCode == "YOG101" && price.Price == 0.55m);
    }

    [Fact]
    public async Task The_next_page_resumes_at_the_rows_already_read()
    {
        var sap = new PagedItemPrices(pricedItems: OrderItems, firstPageRows: 7);
        var client = CreateClient(sap);

        await client.GetItemPricesForCustomerAsync("NRI049", OrderItems);

        // Not $skip=100. Resuming at the page size would step over the eighth item entirely.
        Assert.Equal(["$skip=0", "$skip=7"], sap.ItemQueries.Select(SkipOf));
    }

    [Fact]
    public async Task A_complete_page_costs_one_request()
    {
        var sap = new PagedItemPrices(pricedItems: OrderItems, firstPageRows: 8);
        var client = CreateClient(sap);

        var prices = await client.GetItemPricesForCustomerAsync("NRI049", OrderItems);

        Assert.Equal(8, prices.Count);
        Assert.Single(sap.ItemQueries);
    }

    [Fact]
    public async Task An_empty_page_with_a_continuation_link_does_not_loop()
    {
        var sap = new PagedItemPrices(pricedItems: OrderItems, firstPageRows: 0);
        var client = CreateClient(sap);

        var prices = await client.GetItemPricesForCustomerAsync("NRI049", OrderItems);

        Assert.Empty(prices);
        Assert.Single(sap.ItemQueries);
    }

    [Fact]
    public async Task Every_page_is_ordered_so_the_boundary_is_stable()
    {
        var sap = new PagedItemPrices(pricedItems: OrderItems, firstPageRows: 7);
        var client = CreateClient(sap);

        await client.GetItemPricesForCustomerAsync("NRI049", OrderItems);

        Assert.All(sap.ItemQueries, url => Assert.Contains("$orderby=ItemCode", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_customer_with_no_price_list_is_refused_rather_than_priced_from_list_1()
    {
        // List 1 is the base list and holds 0.00 for a great many items, so defaulting to it turned
        // a business-partner outage into an order full of zeros that read as real prices.
        var sap = new PagedItemPrices(pricedItems: OrderItems, firstPageRows: 8) { PriceListNum = null };
        var client = CreateClient(sap);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetItemPricesForCustomerAsync("NRI049", OrderItems));

        Assert.Contains("NRI049", failure.Message, StringComparison.Ordinal);
        Assert.Empty(sap.ItemQueries);
    }

    private static string SkipOf(string url) =>
        url[url.IndexOf("$skip=", StringComparison.Ordinal)..];

    private static SAPServiceLayerClient CreateClient(PagedItemPrices sap)
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
    /// Serves NRI049 and its items, truncating the first item page to <paramref name="firstPageRows"/>
    /// and attaching a continuation link whenever rows are held back — the Service Layer behaviour
    /// that the old skip arithmetic mishandled.
    /// </summary>
    private sealed class PagedItemPrices(string[] pricedItems, int firstPageRows) : HttpMessageHandler
    {
        public int? PriceListNum { get; init; } = 11;

        public List<string> ItemQueries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));
            }

            if (target.Contains("BusinessPartners(", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    CardCode = "NRI049",
                    CardName = "N Richards Beit Bridge USD",
                    Currency = "USD",
                    PriceListNum
                })));
            }

            ItemQueries.Add(target);

            var skip = IntParam(target, "$skip") ?? 0;
            var served = pricedItems.Skip(skip).Take(skip == 0 ? firstPageRows : pricedItems.Length).ToList();

            var rows = served.Select(itemCode => new
            {
                ItemCode = itemCode,
                ItemName = itemCode,
                ForeignName = (string?)null,
                ItemPrices = new object[]
                {
                    // The real shape: a 0.00 row on the unused base list, the price on list 11.
                    new { PriceList = 1, Price = 0.0, Currency = (string?)null, UoMPrices = Array.Empty<object>() },
                    new { PriceList = 11, Price = 0.55, Currency = (string?)"USD", UoMPrices = Array.Empty<object>() }
                }
            });

            var body = skip + served.Count < pricedItems.Length
                ? JsonSerializer.Serialize(new
                {
                    value = rows,
                    odataNextLink = $"Items?$skip={skip + served.Count}"
                }).Replace("\"odataNextLink\"", "\"odata.nextLink\"", StringComparison.Ordinal)
                : JsonSerializer.Serialize(new { value = rows });

            return Task.FromResult(Json(body));
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
