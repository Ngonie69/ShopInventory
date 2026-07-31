using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
/// Covers the reference data that was re-read from SAP on every request that touched it.
/// </summary>
/// <remarks>
/// Cost centres, currencies and payment terms are configuration — set up once, edited rarely — and
/// the interface documentation for cost centres already said they "rarely change and should be
/// cached locally". They were not. Items were not cached either, while being read per item in a
/// loop on the sales order path.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapReferenceDataCacheTests
{
    [Fact]
    public async Task Cost_centres_are_read_from_sap_once()
    {
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        await client.GetCostCentresAsync();
        await client.GetCostCentresAsync();
        await client.GetCostCentresAsync();

        Assert.Equal(1, sap.CountFor("ProfitCenters"));
    }

    [Fact]
    public async Task Concurrent_first_callers_still_load_cost_centres_once()
    {
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetCostCentresAsync()));

        Assert.Equal(1, sap.CountFor("ProfitCenters"));
    }

    [Fact]
    public async Task Cost_centres_by_dimension_filter_the_cached_list()
    {
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        var dimensionOne = await client.GetCostCentresByDimensionAsync(1);
        var dimensionTwo = await client.GetCostCentresByDimensionAsync(2);

        Assert.Equal(["CC-1"], dimensionOne.Select(centre => centre.CenterCode));
        Assert.Equal(["CC-2"], dimensionTwo.Select(centre => centre.CenterCode));
        Assert.Equal(1, sap.CountFor("ProfitCenters"));
    }

    [Fact]
    public async Task Known_cost_centre_is_answered_without_a_key_lookup()
    {
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        var centre = await client.GetCostCentreByCodeAsync("CC-1");

        Assert.Equal("CC-1", centre!.CenterCode);
        Assert.Equal(0, sap.KeyLookups);
    }

    [Fact]
    public async Task Unknown_cost_centre_still_reaches_sap()
    {
        // The cached list holds only active centres, so a code missing from it may still exist —
        // answering "not found" from the cache alone would hide inactive ones.
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        var centre = await client.GetCostCentreByCodeAsync("CC-INACTIVE");

        Assert.Equal(1, sap.KeyLookups);
        Assert.Equal("CC-INACTIVE", centre!.CenterCode);
    }

    [Fact]
    public async Task Currencies_are_read_from_sap_once()
    {
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        await client.GetCurrenciesAsync();
        await client.GetCurrenciesAsync();

        Assert.Equal(1, sap.CountFor("Currencies"));
    }

    [Fact]
    public async Task Payment_terms_are_cached_per_group()
    {
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        await client.GetPaymentTermsByCodeAsync(1);
        await client.GetPaymentTermsByCodeAsync(1);
        await client.GetPaymentTermsByCodeAsync(2);

        Assert.Equal(2, sap.CountFor("PaymentTermsTypes"));
    }

    [Fact]
    public async Task Items_are_cached_per_code_and_ask_only_for_bound_fields()
    {
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        await client.GetItemByCodeAsync("ITEM-A");
        await client.GetItemByCodeAsync("ITEM-A");
        await client.GetItemByCodeAsync("ITEM-B");

        Assert.Equal(2, sap.CountFor("Items"));
        Assert.All(sap.UrlsFor("Items"), url => Assert.Contains("$select=", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Item_codes_are_cached_case_insensitively()
    {
        // Codes are normalised to upper case before use, so these are the same item.
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        await client.GetItemByCodeAsync("item-a");
        await client.GetItemByCodeAsync("ITEM-A");

        Assert.Equal(1, sap.CountFor("Items"));
    }

    [Fact]
    public async Task Item_names_are_cached_per_code()
    {
        // Held-transfer lines resolve a name per code every time an approver opens the drawer.
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        var first = await client.GetItemNamesAsync(["ITEM-A", "ITEM-B"]);
        var second = await client.GetItemNamesAsync(["ITEM-A", "ITEM-B"]);

        Assert.Equal("Item A", first["ITEM-A"]);
        Assert.Equal("Item B", second["ITEM-B"]);
        Assert.Equal(1, sap.CountFor("Items"));
    }

    [Fact]
    public async Task Only_the_item_names_not_already_cached_are_asked_for()
    {
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        await client.GetItemNamesAsync(["ITEM-A"]);
        var second = await client.GetItemNamesAsync(["item-a", "ITEM-B"]);

        // The known code is matched case-insensitively, as codes are normalised before use, so the
        // second read asks SAP about the new code alone.
        Assert.Equal("Item A", second["ITEM-A"]);
        Assert.Equal(2, sap.CountFor("Items"));
        Assert.DoesNotContain("ITEM-A", sap.UrlsFor("Items")[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_name_that_did_not_come_back_is_asked_for_again()
    {
        // A code SAP had no answer for may be a transient gap rather than a missing item. Caching
        // the blank would hold a bare item code in front of the reader for the full lifetime.
        var sap = new ReferenceDataServer();
        var client = CreateClient(sap);

        await client.GetItemNamesAsync(["ITEM-GONE"]);
        var second = await client.GetItemNamesAsync(["ITEM-GONE"]);

        Assert.Null(second["ITEM-GONE"]);
        Assert.Equal(2, sap.CountFor("Items"));
    }

    private static SAPServiceLayerClient CreateClient(ReferenceDataServer sap)
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

    private sealed class ReferenceDataServer : HttpMessageHandler
    {
        private static readonly Dictionary<string, string> KnownItemNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ITEM-A"] = "Item A",
            ["ITEM-B"] = "Item B"
        };

        private static readonly Regex ItemCodeFilter = new(@"ItemCode eq '([^']+)'", RegexOptions.Compiled);

        private readonly List<string> _urls = [];

        /// <summary>Requests for a single profit centre by key, rather than the list.</summary>
        public int KeyLookups { get; private set; }

        public int CountFor(string entitySet) => UrlsFor(entitySet).Count;

        public List<string> UrlsFor(string entitySet) =>
            [.. _urls.Where(url => url.Contains('/' + entitySet, StringComparison.Ordinal))];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));
            }

            _urls.Add(target);

            if (target.Contains("/ProfitCenters('", StringComparison.Ordinal))
            {
                KeyLookups++;
                return Task.FromResult(Json(
                    "{\"CenterCode\":\"CC-INACTIVE\",\"CenterName\":\"Retired\",\"InWhichDimension\":1,\"Active\":\"tNO\"}"));
            }

            if (target.Contains("/ProfitCenters", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""
                {"value":[
                  {"CenterCode":"CC-1","CenterName":"One","InWhichDimension":1,"Active":"tYES"},
                  {"CenterCode":"CC-2","CenterName":"Two","InWhichDimension":2,"Active":"tYES"}
                ]}
                """));
            }

            if (target.Contains("/Currencies", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"value\":[{\"Code\":\"USD\",\"Name\":\"US Dollar\"}]}"));
            }

            if (target.Contains("/PaymentTermsTypes", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(
                    "{\"GroupNumber\":1,\"PaymentTermsGroupName\":\"Net 30\",\"NumberOfAdditionalDays\":30,\"NumberOfAdditionalMonths\":0}"));
            }

            if (target.Contains("/Items(", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"ItemCode\":\"ITEM-A\",\"ItemName\":\"An item\",\"SalesUnit\":\"EA\"}"));
            }

            if (target.Contains("/Items?", StringComparison.Ordinal))
            {
                // Answers the codes the filter names, and stays silent about the ones it does not
                // know — which is how SAP reports a code that is not in the item master.
                var rows = ItemCodeFilter
                    .Matches(Uri.UnescapeDataString(target))
                    .Select(match => match.Groups[1].Value)
                    .Where(code => KnownItemNames.ContainsKey(code))
                    .Select(code => $"{{\"ItemCode\":\"{code}\",\"ItemName\":\"{KnownItemNames[code]}\"}}");

                return Task.FromResult(Json($"{{\"value\":[{string.Join(",", rows)}]}}"));
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
