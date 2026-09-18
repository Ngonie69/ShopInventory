using System.Diagnostics;
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
/// The per-item, per-warehouse batch and serial reads that sit on invoice and transfer validation.
/// </summary>
/// <remarks>
/// Each used to mint a SQLQueries object per item × warehouse (a permanent row in an OUQR where
/// DELETE does not work), ran with no budget of its own, and read a single page — so SAP's default
/// of 20 rows cut a well-stocked item short without saying so.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class SapItemBatchSerialReadTests
{
    [Fact]
    public async Task Every_item_and_warehouse_shares_one_batch_query_object()
    {
        var sap = new ScriptedServiceLayer();
        var client = CreateClient(sap);

        await client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC");
        await client.GetBatchNumbersForItemInWarehouseAsync("MLK-002", "KEFBYS");
        await client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "HRE01");

        var created = Assert.Single(sap.Created);
        Assert.Equal(SAPServiceLayerClient.ItemBatchesQueryCode, created.Code);
        Assert.Equal("ITEM_BATCHES_V1", created.Code);
        Assert.Equal(3, sap.ExecutedQueries.Count);
        Assert.All(sap.ExecutedQueries, url => Assert.StartsWith("/b1s/v1/SQLQueries('ITEM_BATCHES_V1')/List", url));

        foreach (var value in new[] { "CHE011", "MLK-002", "KEFBYC", "KEFBYS", "HRE01" })
        {
            Assert.DoesNotContain(value, created.SqlText, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(":itemCode", created.SqlText, StringComparison.Ordinal);
        Assert.Contains(":whsCode", created.SqlText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_item_and_warehouse_shares_one_serial_query_object()
    {
        var sap = new ScriptedServiceLayer();
        var client = CreateClient(sap);

        await client.GetSerialNumbersForItemInWarehouseAsync("FRG100", "KEFBYC");
        await client.GetSerialNumbersForItemInWarehouseAsync("FRG200", "KEFBYS");

        var created = Assert.Single(sap.Created);
        Assert.Equal("ITEM_SERIALS_V1", created.Code);
        Assert.Equal(2, sap.ExecutedQueries.Count);

        foreach (var value in new[] { "FRG100", "FRG200", "KEFBYC", "KEFBYS" })
        {
            Assert.DoesNotContain(value, created.SqlText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Item_and_warehouse_reach_sap_as_bound_values_on_the_list_url()
    {
        var sap = new ScriptedServiceLayer();
        var client = CreateClient(sap);

        await client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC");
        await client.GetSerialNumbersForItemInWarehouseAsync("FRG'100", "KEFBYS");

        Assert.Equal(2, sap.ExecutedQueries.Count);
        Assert.Contains("itemCode='CHE011'", sap.ExecutedQueries[0], StringComparison.Ordinal);
        Assert.Contains("whsCode='KEFBYC'", sap.ExecutedQueries[0], StringComparison.Ordinal);
        // A quote in a code is doubled, the SQL literal escape, not left to end the value early.
        Assert.Contains("itemCode='FRG''100'", sap.ExecutedQueries[1], StringComparison.Ordinal);
        Assert.Contains("whsCode='KEFBYS'", sap.ExecutedQueries[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batches_past_sap_default_page_of_twenty_are_all_returned()
    {
        var firstPage = Enumerable.Range(1, 20).Select(index => BatchRow($"B{index:D3}", 1)).ToList();
        var secondPage = Enumerable.Range(21, 5).Select(index => BatchRow($"B{index:D3}", 1)).ToList();
        var sap = new ScriptedServiceLayer
        {
            Pages =
            [
                $"{{\"value\":[{string.Join(",", firstPage)}],\"odata.nextLink\":\"SQLQueries('ITEM_BATCHES_V1')/List?$skip=20\"}}",
                $"{{\"value\":[{string.Join(",", secondPage)}]}}"
            ]
        };
        var client = CreateClient(sap);

        var batches = await client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC");

        Assert.Equal(25, batches.Count);
        Assert.Equal("B001", batches[0].BatchNum);
        Assert.Equal("B025", batches[^1].BatchNum);
        Assert.Equal(2, sap.ExecutedQueries.Count);
        Assert.Contains("$skip=0", sap.ExecutedQueries[0], StringComparison.Ordinal);
        Assert.Contains("$skip=20", sap.ExecutedQueries[1], StringComparison.Ordinal);
        // Both pages keep the item and warehouse bound, and ask for more than SAP's default page.
        Assert.All(sap.ExecutedQueries, url => Assert.Contains("itemCode='CHE011'", url, StringComparison.Ordinal));
        Assert.All(sap.PreferHeaders, prefer => Assert.Equal("odata.maxpagesize=500", prefer));
    }

    [Fact]
    public async Task Serials_past_sap_default_page_of_twenty_are_all_returned()
    {
        var firstPage = Enumerable.Range(1, 20).Select(SerialRow).ToList();
        var secondPage = Enumerable.Range(21, 3).Select(SerialRow).ToList();
        var sap = new ScriptedServiceLayer
        {
            Pages =
            [
                $"{{\"value\":[{string.Join(",", firstPage)}],\"@odata.nextLink\":\"next\"}}",
                $"{{\"value\":[{string.Join(",", secondPage)}]}}"
            ]
        };
        var client = CreateClient(sap);

        var serials = await client.GetSerialNumbersForItemInWarehouseAsync("FRG100", "KEFBYC");

        Assert.Equal(23, serials.Count);
        Assert.Equal(23, serials[^1].SystemNumber);
    }

    /// <summary>
    /// The rows now arrive as dictionaries rather than JSON, so the mapping has to be proved to give
    /// what the JSON parser gave — which is still the one the warehouse-wide batch read uses.
    /// </summary>
    [Fact]
    public async Task Batch_rows_map_exactly_as_the_json_parser_maps_them()
    {
        const string rows = """
            [
              {"ItemCode":"CHE011","BatchNum":"B2401","Quantity":12.5,"WhsCode":"OTHER","ExpDate":"2026-12-31T00:00:00Z","MnfDate":"2026-01-02T00:00:00Z","InDate":"2026-01-05T00:00:00Z","Notes":"Chilled"},
              {"ItemCode":"CHE011","BatchNum":"B2402","Quantity":3,"WhsCode":"OTHER","ExpDate":null,"MnfDate":null,"InDate":"20260106","Notes":null},
              {"ItemCode":"CHE011","BatchNum":"B2403","Quantity":0.0625,"WhsCode":"OTHER"}
            ]
            """;
        var sap = new ScriptedServiceLayer { Pages = [$"{{\"value\":{rows}}}"] };
        var client = CreateClient(sap);

        var mapped = await client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC");
        var parsed = client.ParseBatchNumbersFromSqlResult($"{{\"value\":{rows}}}", "KEFBYC");

        Assert.Equal(3, parsed.Count);
        Assert.Equal(
            parsed.Select(Describe),
            mapped.Select(Describe));

        Assert.Equal(12.5m, mapped[0].Quantity);
        Assert.Equal(3m, mapped[1].Quantity);
        Assert.Equal(0.0625m, mapped[2].Quantity);
        Assert.Equal("2026-12-31T00:00:00Z", mapped[0].ExpiryDate);
        Assert.Null(mapped[1].ExpiryDate);
        Assert.Null(mapped[1].Notes);
        Assert.Null(mapped[2].ManufacturingDate);
        // The warehouse is the one asked about, not whatever the row carries.
        Assert.All(mapped, batch => Assert.Equal("KEFBYC", batch.Warehouse));

        static string Describe(ShopInventory.Models.BatchNumber batch) =>
            string.Join("|",
                batch.ItemCode, batch.ItemName, batch.BatchNum,
                batch.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                batch.Warehouse, batch.ExpiryDate, batch.ManufacturingDate, batch.AdmissionDate, batch.Notes,
                batch.Status, batch.Location, batch.InternalSerialNumber, batch.ManufacturerSerialNumber);
    }

    [Fact]
    public async Task Serial_rows_map_field_for_field()
    {
        const string rows = """
            [
              {"ItemCode":"FRG100","DistNumber":"SN-1","Quantity":1,"WhsCode":"OTHER","SystemNumber":4411,"InternalSerialNumber":"INT-1","ManufacturerSerialNumber":"MFR-1"},
              {"ItemCode":"FRG100","DistNumber":"SN-2","Quantity":1.0,"WhsCode":"OTHER","SystemNumber":4412,"InternalSerialNumber":null,"ManufacturerSerialNumber":null}
            ]
            """;
        var sap = new ScriptedServiceLayer { Pages = [$"{{\"value\":{rows}}}"] };
        var client = CreateClient(sap);

        var serials = await client.GetSerialNumbersForItemInWarehouseAsync("FRG100", "KEFBYC");

        Assert.Equal(2, serials.Count);
        Assert.Equal("FRG100", serials[0].ItemCode);
        Assert.Equal("SN-1", serials[0].DistNumber);
        Assert.Equal(1m, serials[0].Quantity);
        Assert.Equal(4411, serials[0].SystemNumber);
        Assert.Equal("INT-1", serials[0].InternalSerialNumber);
        Assert.Equal("MFR-1", serials[0].ManufacturerSerialNumber);
        Assert.Equal(1.0m, serials[1].Quantity);
        Assert.Equal(4412, serials[1].SystemNumber);
        Assert.Null(serials[1].InternalSerialNumber);
        Assert.Null(serials[1].ManufacturerSerialNumber);
        Assert.All(serials, serial => Assert.Equal("KEFBYC", serial.WhsCode));
    }

    [Fact]
    public async Task No_matching_records_reads_as_no_batches()
    {
        var sap = new ScriptedServiceLayer
        {
            ListStatus = HttpStatusCode.NotFound,
            Pages = ["{\"error\":{\"code\":-2028,\"message\":{\"lang\":\"en-us\",\"value\":\"No matching records found (ODBC -2028)\"}}}"]
        };
        var client = CreateClient(sap);

        Assert.Empty(await client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC"));
    }

    [Fact]
    public async Task A_sap_error_on_the_batch_read_is_still_an_invalid_operation()
    {
        var sap = new ScriptedServiceLayer
        {
            ListStatus = HttpStatusCode.BadRequest,
            Pages = ["{\"error\":{\"code\":-1,\"message\":{\"lang\":\"en-us\",\"value\":\"Boom\"}}}"]
        };
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC"));
    }

    [Fact]
    public async Task A_hung_batch_read_gives_up_at_its_budget_as_a_timeout()
    {
        var sap = new ScriptedServiceLayer { HangOnList = true };
        var client = CreateClient(sap, budgetSeconds: 5);
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC"));

        clock.Stop();
        Assert.Contains("5-second budget", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(30));
        Assert.True(SapFailureClassifier.IsTransient(ex));
    }

    [Fact]
    public async Task Caller_cancellation_of_a_batch_read_stays_a_cancellation()
    {
        var sap = new ScriptedServiceLayer { HangOnList = true };
        var client = CreateClient(sap, budgetSeconds: 60);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetBatchNumbersForItemInWarehouseAsync("CHE011", "KEFBYC", caller.Token));

        Assert.IsNotType<TimeoutException>(ex);
    }

    /// <summary>
    /// The serial read has always answered any failure with an empty list, and still does — the
    /// budget changes only how long it waits before doing so.
    /// </summary>
    [Fact]
    public async Task A_hung_serial_read_gives_up_at_its_budget_and_answers_empty()
    {
        var sap = new ScriptedServiceLayer { HangOnList = true };
        var client = CreateClient(sap, budgetSeconds: 5);
        var clock = Stopwatch.StartNew();

        var serials = await client.GetSerialNumbersForItemInWarehouseAsync("FRG100", "KEFBYC");

        clock.Stop();
        Assert.Empty(serials);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(30));
    }

    private static string BatchRow(string batch, decimal quantity) =>
        $"{{\"ItemCode\":\"CHE011\",\"BatchNum\":\"{batch}\",\"Quantity\":{quantity},\"WhsCode\":\"KEFBYC\",\"ExpDate\":null,\"MnfDate\":null,\"InDate\":null,\"Notes\":null}}";

    private static string SerialRow(int index) =>
        $"{{\"ItemCode\":\"FRG100\",\"DistNumber\":\"SN-{index}\",\"Quantity\":1,\"WhsCode\":\"KEFBYC\",\"SystemNumber\":{index},\"InternalSerialNumber\":\"INT-{index}\",\"ManufacturerSerialNumber\":null}}";

    private static SAPServiceLayerClient CreateClient(ScriptedServiceLayer sap, int budgetSeconds = 60)
    {
        var httpClient = new HttpClient(sap)
        {
            BaseAddress = new Uri("https://sap.invalid/b1s/v1/")
        };

        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings
            {
                ServiceLayerUrl = "https://sap.invalid/b1s/v1/",
                StockSqlRequestTimeoutSeconds = budgetSeconds
            }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            new StubItemUomMappingStore());
    }

    /// <summary>
    /// Records every query object created and every List executed, and answers the List with
    /// <see cref="Pages"/> in turn. The existence probe answers 404, so each statement is created.
    /// </summary>
    private sealed class ScriptedServiceLayer : HttpMessageHandler
    {
        private int _page;

        /// <summary>Whole response bodies for successive List calls; the last repeats.</summary>
        public List<string> Pages { get; init; } = ["{\"value\":[]}"];

        public HttpStatusCode ListStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>Accept the List request and never answer it.</summary>
        public bool HangOnList { get; init; }

        public List<(string Code, string SqlText)> Created { get; } = [];

        /// <summary>Path and query of each List call, unescaped so assertions read the bound values.</summary>
        public List<string> ExecutedQueries { get; } = [];

        public List<string> PreferHeaders { get; } = [];

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
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
                Created.Add((body.GetProperty("SqlCode").GetString()!, body.GetProperty("SqlText").GetString()!));
                return Json("{}", HttpStatusCode.Created);
            }

            if (path.EndsWith("/List", StringComparison.Ordinal))
            {
                ExecutedQueries.Add(Uri.UnescapeDataString(request.RequestUri.PathAndQuery));
                PreferHeaders.Add(string.Join(",", request.Headers.TryGetValues("Prefer", out var prefer) ? prefer : []));

                if (HangOnList)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                var body = Pages[Math.Min(_page, Pages.Count - 1)];
                _page++;
                return Json(body, ListStatus);
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
