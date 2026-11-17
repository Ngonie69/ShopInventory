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
/// Covers <see cref="SAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync"/> reading item stock
/// with a $crossjoin instead of a SQLQueries('STK_ITEMS_…') object.
/// </summary>
/// <remarks>
/// A SQLQueries read can leave a Service Layer worker spinning at 100% CPU for hours after the caller has
/// gone; on 17 Nov 2026 one held 1.8 GB on "YOG in VAN010". Entity and $crossjoin reads never did. These
/// tests pin that the read no longer touches SQLQueries at all, and that it returns what the SQL did: every
/// requested item's row in the warehouse, matched case-insensitively, with Available worked out the same way.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class ItemStockCrossJoinTests
{
    private static readonly StockRow[] VanRows =
    [
        new("YOG001", "1Kg Aloe Vera Green Tea Yoghurt", "6001234000011", "Each", 12, 2, 5),
        new("YOG002", "1Kg Aloe Vera Orange Yoghurt", null, "Each", 0, 0, 0),
        new("YOG003", "1Kg Banana Smooth Yoghurt", null, "Each", 7.5m, 0, 0),
    ];

    [Fact]
    public async Task Stock_is_read_with_a_crossjoin_and_never_through_SQLQueries()
    {
        var sap = new ItemStockSap(VanRows);

        await CreateClient(sap).GetStockQuantitiesForItemsInWarehouseAsync("VAN010", ["YOG001"]);

        Assert.DoesNotContain(sap.Requests, url => url.Contains("SQLQueries", StringComparison.Ordinal));
        Assert.Single(sap.StockReads);
        Assert.StartsWith("/b1s/v1/$crossjoin(Items,Items/ItemWarehouseInfoCollection)", sap.StockReads[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_requested_items_come_back_matched_without_regard_to_case()
    {
        var sap = new ItemStockSap(VanRows);

        var stock = await CreateClient(sap).GetStockQuantitiesForItemsInWarehouseAsync("VAN010", ["yog001", "YOG003"]);

        Assert.Equal(["YOG001", "YOG003"], stock.Select(row => row.ItemCode));
        var first = stock[0];
        Assert.Equal("1Kg Aloe Vera Green Tea Yoghurt", first.ItemName);
        Assert.Equal("6001234000011", first.BarCode);
        Assert.Equal("VAN010", first.WarehouseCode);
        Assert.Equal("Each", first.UoM);
        Assert.Equal(12m, first.InStock);
        Assert.Equal(2m, first.Committed);
        Assert.Equal(5m, first.Ordered);
        Assert.Equal(15m, first.Available);
        Assert.Equal(7.5m, stock[1].InStock);
    }

    [Fact]
    public async Task Every_family_of_a_batch_goes_in_one_read_scoped_to_the_warehouse()
    {
        var sap = new ItemStockSap(VanRows);

        await CreateClient(sap).GetStockQuantitiesForItemsInWarehouseAsync("VAN010", ["YOG001", "che011", "PIC003"]);

        var filter = Uri.UnescapeDataString(Assert.Single(sap.StockReads));
        Assert.Contains("Items/ItemWarehouseInfoCollection/WarehouseCode eq 'VAN010'", filter, StringComparison.Ordinal);
        Assert.Contains("startswith(Items/ItemCode,'CHE')", filter, StringComparison.Ordinal);
        Assert.Contains("startswith(Items/ItemCode,'PIC')", filter, StringComparison.Ordinal);
        Assert.Contains("startswith(Items/ItemCode,'YOG')", filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_short_page_resumes_at_the_rows_already_read()
    {
        var sap = new ItemStockSap(VanRows, firstPageRows: 2);

        var stock = await CreateClient(sap).GetStockQuantitiesForItemsInWarehouseAsync("VAN010", ["YOG001", "YOG002", "YOG003"]);

        Assert.Equal(3, stock.Count);
        Assert.Equal(2, sap.StockReads.Count);
        Assert.DoesNotContain("$skip=", sap.StockReads[0], StringComparison.Ordinal);
        Assert.EndsWith("&$skip=2", sap.StockReads[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_success_fails_the_read_as_the_SQL_read_did()
    {
        var sap = new ItemStockSap(VanRows) { Failure = HttpStatusCode.BadRequest };

        var failure = await Assert.ThrowsAsync<SapReadFailedException>(
            () => CreateClient(sap).GetStockQuantitiesForItemsInWarehouseAsync("VAN010", ["YOG001"]));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_packaging_fields_are_selected_only_when_the_company_has_them(bool useCustomFields)
    {
        var query = Uri.UnescapeDataString(SAPServiceLayerClient.BuildItemStockCrossJoin("VAN010", ["YOG"], useCustomFields));

        Assert.Contains("Items($select=ItemCode,ItemName,BarCode,InventoryUOM", query, StringComparison.Ordinal);
        Assert.Equal(useCustomFields, query.Contains("U_PackagingCode", StringComparison.Ordinal));
    }

    [Fact]
    public void A_quote_in_a_code_is_escaped_rather_than_ending_the_literal()
    {
        var query = Uri.UnescapeDataString(SAPServiceLayerClient.BuildItemStockCrossJoin("O'NEIL", ["A'B"], useCustomFields: false));

        Assert.Contains("WarehouseCode eq 'O''NEIL'", query, StringComparison.Ordinal);
        Assert.Contains("startswith(Items/ItemCode,'A''B')", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_fields_are_read_when_the_row_carries_them()
    {
        using var page = JsonDocument.Parse("""
            {"value":[{"Items":{"ItemCode":"PYG058","ItemName":"Lid","BarCode":null,"InventoryUOM":"Each",
              "U_PackagingCode":"PK1","U_PackagingCodeLabels":"LB1","U_PackagingCodeLids":"LD1"},
              "Items/ItemWarehouseInfoCollection":{"WarehouseCode":"KEFGRS","InStock":3.0,"Committed":1.0,"Ordered":0.0}}]}
            """);

        var row = Assert.Single(SAPServiceLayerClient.ParseItemStockCrossJoinPage(page.RootElement));

        Assert.Equal(("PK1", "LB1", "LD1"), (row.PackagingCode, row.PackagingCodeLabels, row.PackagingCodeLids));
        Assert.Equal(2m, row.Available);
    }

    private sealed record StockRow(string ItemCode, string ItemName, string? BarCode, string UoM, decimal InStock, decimal Committed, decimal Ordered);

    private static SAPServiceLayerClient CreateClient(ItemStockSap sap)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/", UseCustomFields = false }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    /// <summary>
    /// Answers the item stock $crossjoin with <paramref name="rows"/> in VAN010, the first page cut to
    /// <paramref name="firstPageRows"/> with a continuation link, as the Service Layer may do.
    /// </summary>
    private sealed class ItemStockSap(StockRow[] rows, int? firstPageRows = null) : HttpMessageHandler
    {
        public HttpStatusCode? Failure { get; init; }

        public List<string> Requests { get; } = [];

        public List<string> StockReads { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;
            Requests.Add(target);

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));
            }

            if (!target.Contains("$crossjoin(Items,Items/ItemWarehouseInfoCollection)", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            StockReads.Add(target);
            if (Failure is { } status)
            {
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("{\"error\":{\"code\":-1,\"message\":{\"value\":\"refused\"}}}", Encoding.UTF8, "application/json")
                });
            }

            var skipAt = target.IndexOf("&$skip=", StringComparison.Ordinal);
            var skip = skipAt < 0 ? 0 : int.Parse(target[(skipAt + "&$skip=".Length)..]);
            var served = rows.Skip(skip).Take(skip == 0 && firstPageRows is { } cut ? cut : rows.Length).ToList();

            var page = served.Select(row => new Dictionary<string, object?>
            {
                ["Items"] = new { row.ItemCode, row.ItemName, row.BarCode, InventoryUOM = row.UoM },
                ["Items/ItemWarehouseInfoCollection"] = new { WarehouseCode = "VAN010", row.InStock, row.Committed, row.Ordered }
            });

            var body = skip + served.Count < rows.Length
                ? JsonSerializer.Serialize(new { value = page, odataNextLink = "$crossjoin(Items,Items/ItemWarehouseInfoCollection)?$skip=x" })
                    .Replace("\"odataNextLink\"", "\"odata.nextLink\"", StringComparison.Ordinal)
                : JsonSerializer.Serialize(new { value = page });

            return Task.FromResult(Json(body));
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
