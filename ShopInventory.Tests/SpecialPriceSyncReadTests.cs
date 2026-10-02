using System.Net;
using System.Text;
using System.Text.Json;
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
/// Covers the full special price read behind the special price sync.
/// </summary>
/// <remarks>
/// It used to read all 11,206 SpecialPrices records on KEFALOS_TEST_3 to keep 9. It now asks SAP for
/// the rows whose header window covers today, plus — through <c>$crossjoin</c>, because SAP refuses
/// <c>any()</c> — the records with a period row in force. The danger is a record whose header has
/// expired but whose period row is current: the header filter cannot see it, and losing it charges
/// the customer list price.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SpecialPriceSyncReadTests
{
    private static readonly object HeaderCurrent = Record("C001", "YOG001", 2.50, validFrom: null, validTo: null);

    // Header ended in 2021; its period row has been open-ended since 2020, so SAP still charges 1.75.
    private static readonly object PeriodOnly = Record(
        "C002 USD", "YOG002", 9.99, validFrom: "2020-01-01T00:00:00Z", validTo: "2021-12-31T00:00:00Z",
        areas: [new { DateFrom = "2020-01-01", Dateto = (string?)null, SpecialPrice = 1.75 }]);

    [Fact]
    public async Task A_price_current_only_through_its_period_row_is_kept()
    {
        var sap = new FakeSpecialPrices(headerRows: [HeaderCurrent], periodKeys: [("C002 USD", "YOG002")], all: [HeaderCurrent, PeriodOnly]);

        var prices = await CreateClient(sap).GetAllSpecialPricesAsync();

        Assert.Equal(2, prices.Count);
        var period = Assert.Single(prices, price => price.CardCode == "C002 USD");
        Assert.Equal(1.75m, period.Price);
        Assert.Contains(sap.Queries, query => query.Kind == "key");
    }

    [Fact]
    public async Task Every_special_price_is_never_read_when_the_filters_work()
    {
        var sap = new FakeSpecialPrices(headerRows: [HeaderCurrent], periodKeys: [("C002 USD", "YOG002")], all: [HeaderCurrent, PeriodOnly]);

        await CreateClient(sap).GetAllSpecialPricesAsync();

        Assert.DoesNotContain(sap.Queries, query => query.Kind == "all");
    }

    [Fact]
    public async Task A_period_record_the_header_read_already_returned_is_not_read_again()
    {
        var sap = new FakeSpecialPrices(headerRows: [HeaderCurrent], periodKeys: [("C001", "YOG001")], all: [HeaderCurrent]);

        var prices = await CreateClient(sap).GetAllSpecialPricesAsync();

        Assert.Single(prices);
        Assert.DoesNotContain(sap.Queries, query => query.Kind == "key");
    }

    [Fact]
    public async Task A_refused_period_query_falls_back_to_reading_everything()
    {
        var sap = new FakeSpecialPrices(headerRows: [HeaderCurrent], periodKeys: [], all: [HeaderCurrent, PeriodOnly])
        {
            RefuseCrossJoin = true
        };

        var prices = await CreateClient(sap).GetAllSpecialPricesAsync();

        Assert.Equal(2, prices.Count);
        Assert.Contains(sap.Queries, query => query.Kind == "all");
    }

    [Fact]
    public async Task A_short_page_resumes_at_the_rows_read()
    {
        var rows = Enumerable.Range(1, 8)
            .Select(index => Record($"C{index:000}", "YOG001", 1.0 + index, validFrom: null, validTo: null))
            .ToArray();
        var sap = new FakeSpecialPrices(headerRows: rows, periodKeys: [], all: rows) { FirstHeaderPageRows = 5 };

        var prices = await CreateClient(sap).GetAllSpecialPricesAsync();

        Assert.Equal(8, prices.Count);
        Assert.Equal(["$skip=0", "$skip=5"], sap.Queries.Where(query => query.Kind == "header").Select(query => SkipOf(query.Url)));
    }

    [Fact]
    public void The_header_filter_reaches_a_day_either_side_of_today()
    {
        var filter = SAPServiceLayerClient.BuildCurrentSpecialPriceHeaderFilter(new DateTime(2026, 10, 2));

        Assert.Equal(
            "(ValidFrom eq null or ValidFrom le '2026-10-03') and (ValidTo eq null or ValidTo ge '2026-10-01')",
            filter);
    }

    [Fact]
    public void The_period_query_joins_each_period_row_to_its_own_record()
    {
        var query = Uri.UnescapeDataString(
            SAPServiceLayerClient.BuildCurrentSpecialPricePeriodCrossJoin(new DateTime(2026, 10, 2)));

        Assert.StartsWith("$crossjoin(SpecialPrices,SpecialPrices/SpecialPriceDataAreas)?", query, StringComparison.Ordinal);
        Assert.Contains("SpecialPrices/CardCode eq SpecialPrices/SpecialPriceDataAreas/BPCode", query, StringComparison.Ordinal);
        Assert.Contains("SpecialPrices/ItemCode eq SpecialPrices/SpecialPriceDataAreas/ItemNo", query, StringComparison.Ordinal);
        Assert.Contains("(SpecialPrices/SpecialPriceDataAreas/DateFrom eq null or SpecialPrices/SpecialPriceDataAreas/DateFrom le '2026-10-03')", query, StringComparison.Ordinal);
        Assert.Contains("(SpecialPrices/SpecialPriceDataAreas/Dateto eq null or SpecialPrices/SpecialPriceDataAreas/Dateto ge '2026-10-01')", query, StringComparison.Ordinal);
        Assert.DoesNotContain("any(", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_filters_stay_short_and_cover_every_key()
    {
        var keys = Enumerable.Range(1, 200).Select(index => ($"CARD{index:000} USD", $"ITEM{index:000}")).ToList();

        var filters = SAPServiceLayerClient.BuildSpecialPriceKeyFilters(keys);

        Assert.True(filters.Count > 1);
        Assert.All(filters, filter => Assert.True(filter.Length <= 1200, $"{filter.Length} characters"));
        Assert.Equal(200, filters.Sum(filter => Regex.Matches(filter, "CardCode eq ").Count));
    }

    [Fact]
    public void Key_filters_escape_quotes_instead_of_refusing_them()
    {
        var filter = Assert.Single(SAPServiceLayerClient.BuildSpecialPriceKeyFilters([("O'BRIEN", "YOG001")]));

        Assert.Equal("(CardCode eq 'O''BRIEN' and ItemCode eq 'YOG001')", filter);
    }

    private static object Record(
        string cardCode,
        string itemCode,
        double price,
        string? validFrom,
        string? validTo,
        object[]? areas = null) =>
        new
        {
            ItemCode = itemCode,
            CardCode = cardCode,
            Price = price,
            Valid = "tYES",
            ValidFrom = validFrom,
            ValidTo = validTo,
            SpecialPriceDataAreas = areas ?? []
        };

    private static string SkipOf(string url) =>
        url[url.IndexOf("$skip=", StringComparison.Ordinal)..];

    private static SAPServiceLayerClient CreateClient(FakeSpecialPrices sap)
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
    /// Answers each of the four reads with canned rows: the header-window read, the period crossjoin,
    /// a read by key, and the unfiltered read. It does not evaluate the filters — the live comparison
    /// against KEFALOS_TEST_3 is what proves SAP applies them as intended.
    /// </summary>
    private sealed class FakeSpecialPrices(object[] headerRows, (string CardCode, string ItemCode)[] periodKeys, object[] all)
        : HttpMessageHandler
    {
        public bool RefuseCrossJoin { get; init; }

        public int? FirstHeaderPageRows { get; init; }

        public List<(string Kind, string Url)> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);

            if (target.EndsWith("/Login", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));

            var skip = int.Parse(Regex.Match(target, @"\$skip=(\d+)").Groups[1].Value);

            if (target.Contains("$crossjoin(", StringComparison.Ordinal))
            {
                Queries.Add(("crossjoin", target));
                if (RefuseCrossJoin)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":{\"code\":201,\"message\":{\"value\":\"Query string error\"}}}")
                    });
                }

                var rows = periodKeys.Skip(skip).Select(key => new
                {
                    SpecialPrices = new { key.CardCode, key.ItemCode },
                    SpecialPricesSpecialPriceDataAreas = new { DateFrom = "2020-01-01", Dateto = (string?)null }
                });
                return Task.FromResult(Json(JsonSerializer.Serialize(new { value = rows })));
            }

            if (target.Contains("ValidFrom eq null", StringComparison.Ordinal))
            {
                Queries.Add(("header", target));
                var take = skip == 0 && FirstHeaderPageRows is { } first ? first : headerRows.Length;
                var page = headerRows.Skip(skip).Take(take).ToArray();
                return Task.FromResult(Json(Page(page, more: skip + page.Length < headerRows.Length)));
            }

            if (target.Contains("$filter=", StringComparison.Ordinal))
            {
                Queries.Add(("key", target));
                var requested = Regex.Matches(target, @"CardCode eq '((?:[^']|'')*)' and ItemCode eq '((?:[^']|'')*)'")
                    .Select(match => (match.Groups[1].Value.Replace("''", "'"), match.Groups[2].Value.Replace("''", "'")))
                    .ToHashSet();
                var matches = all.Where(row => requested.Contains(KeyOf(row))).Skip(skip).ToArray();
                return Task.FromResult(Json(Page(matches, more: false)));
            }

            Queries.Add(("all", target));
            return Task.FromResult(Json(Page(all.Skip(skip).ToArray(), more: false)));
        }

        private static (string, string) KeyOf(object row)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(row));
            return (doc.RootElement.GetProperty("CardCode").GetString()!, doc.RootElement.GetProperty("ItemCode").GetString()!);
        }

        private static string Page(object[] rows, bool more) =>
            more
                ? JsonSerializer.Serialize(new { value = rows, odataNextLink = "SpecialPrices?$skip=next" })
                    .Replace("\"odataNextLink\"", "\"odata.nextLink\"", StringComparison.Ordinal)
                : JsonSerializer.Serialize(new { value = rows });

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
