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
/// The van sales approved catalogue: the items flagged <c>U_VanSale = 'Yes'</c> on the item master.
/// </summary>
/// <remarks>
/// Two properties matter. The approval list narrows the warehouse page <em>before</em> the skip/take,
/// or a page of van products comes back short and <c>HasMore</c> counts items the van may not sell.
/// And the statement is a fixed literal, so it resolves to one <c>SQLQueries</c> object however many
/// times it is asked for — a code that varied would be a permanent leak, since DELETE never completes.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class VanSalesApprovedCatalogueTests
{
    [Fact]
    public async Task The_catalogue_reads_the_van_sale_flag_from_the_item_master()
    {
        var sap = new RecordingServiceLayer
        {
            VanSaleRows = """[{"ItemCode":"CHE011"},{"ItemCode":"NRI049"}]"""
        };
        var client = CreateClient(sap);

        var approved = await client.GetVanSalesApprovedItemCodesAsync();

        Assert.Equal(["CHE011", "NRI049"], approved.OrderBy(code => code, StringComparer.Ordinal));
        Assert.Contains("U_VanSale", sap.CreatedStatements.Single(), StringComparison.Ordinal);
        Assert.Contains("'Yes'", sap.CreatedStatements.Single(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A code is matched case-insensitively, because nothing guarantees the warehouse stock query and
    /// the item master hand back the same casing for the same item.
    /// </summary>
    [Fact]
    public async Task An_approved_code_matches_whatever_case_it_is_stored_in()
    {
        var sap = new RecordingServiceLayer { VanSaleRows = """[{"ItemCode":"che011"}]""" };
        var client = CreateClient(sap);

        Assert.Contains("CHE011", await client.GetVanSalesApprovedItemCodesAsync());
    }

    [Fact]
    public async Task Repeated_reads_share_one_query_object_and_one_execution()
    {
        var sap = new RecordingServiceLayer { VanSaleRows = """[{"ItemCode":"CHE011"}]""" };
        var client = CreateClient(sap);

        await client.GetVanSalesApprovedItemCodesAsync();
        await client.GetVanSalesApprovedItemCodesAsync();
        await client.GetVanSalesApprovedItemCodesAsync();

        Assert.Single(sap.CreatedCodes);
        Assert.Single(sap.ExecutedVanSaleQueries);
    }

    /// <summary>
    /// The filter runs before the skip/take. Asking for two van products out of a warehouse whose
    /// first rows are not approved has to return two, not "whatever survived the first two rows".
    /// </summary>
    [Fact]
    public async Task The_page_is_filled_from_approved_items_only()
    {
        var sap = new RecordingServiceLayer
        {
            WarehouseRows = """[{"ItemCode":"BLK001"},{"ItemCode":"BLK002"},{"ItemCode":"CHE011"},{"ItemCode":"NRI049"},{"ItemCode":"PIC003"}]""",
            VanSaleRows = """[{"ItemCode":"CHE011"},{"ItemCode":"NRI049"},{"ItemCode":"PIC003"}]"""
        };
        var client = CreateClient(sap);

        var (items, hasMore) = await client.GetPagedItemsInWarehouseAsync("MSA", page: 1, pageSize: 2, vanSaleOnly: true);

        Assert.Equal(["CHE011", "NRI049"], items.Select(item => item.ItemCode));
        // Three approved, two taken: one left, not the three the unfiltered list would have implied.
        Assert.True(hasMore);
    }

    [Fact]
    public async Task The_unfiltered_page_never_asks_for_the_catalogue()
    {
        var sap = new RecordingServiceLayer
        {
            WarehouseRows = """[{"ItemCode":"BLK001"},{"ItemCode":"CHE011"}]""",
            VanSaleRows = """[{"ItemCode":"CHE011"}]"""
        };
        var client = CreateClient(sap);

        var (items, _) = await client.GetPagedItemsInWarehouseAsync("MSA", page: 1, pageSize: 20);

        Assert.Equal(["BLK001", "CHE011"], items.Select(item => item.ItemCode));
        Assert.Empty(sap.ExecutedVanSaleQueries);
    }

    /// <summary>
    /// Approved but not carried is the ordinary case — the flag is an approval, not a stock level —
    /// so an empty intersection is an empty page rather than a fall-back to everything.
    /// </summary>
    [Fact]
    public async Task A_van_carrying_nothing_approved_gets_an_empty_page()
    {
        var sap = new RecordingServiceLayer
        {
            WarehouseRows = """[{"ItemCode":"BLK001"}]""",
            VanSaleRows = """[{"ItemCode":"CHE011"}]"""
        };
        var client = CreateClient(sap);

        var (items, hasMore) = await client.GetPagedItemsInWarehouseAsync("MSA", page: 1, pageSize: 20, vanSaleOnly: true);

        Assert.Empty(items);
        Assert.False(hasMore);
    }

    // ── The catalogue standing on its own ───────────────────────────────────

    /// <summary>
    /// The catalogue read is the approval list resolved to item rows, and narrowed by nothing else.
    /// </summary>
    [Fact]
    public async Task The_catalogue_resolves_every_approved_code_to_an_item()
    {
        var sap = new RecordingServiceLayer
        {
            VanSaleRows = """[{"ItemCode":"NRI049"},{"ItemCode":"CHE011"}]"""
        };
        var client = CreateClient(sap);

        var items = await client.GetVanSalesApprovedItemsAsync();

        Assert.Equal(["CHE011", "NRI049"], items.Select(item => item.ItemCode));
        Assert.Equal(["CHE011 name", "NRI049 name"], items.Select(item => item.ItemName));
    }

    /// <summary>
    /// The point of the whole thing: a stock transfer request is for what the van has none of, so an
    /// item no warehouse is carrying has to survive the read. Every other path over this flag
    /// intersects it with stock, and would drop this item.
    /// </summary>
    [Fact]
    public async Task The_catalogue_holds_items_nothing_is_carrying()
    {
        var sap = new RecordingServiceLayer
        {
            WarehouseRows = "[]",
            VanSaleRows = """[{"ItemCode":"CHE011"}]"""
        };
        var client = CreateClient(sap);

        var items = await client.GetVanSalesApprovedItemsAsync();

        Assert.Equal(["CHE011"], items.Select(item => item.ItemCode));
    }

    /// <summary>
    /// Nothing approved is an empty catalogue, and no item master read at all — a request for zero
    /// codes comes back as every item there is.
    /// </summary>
    [Fact]
    public async Task An_empty_approval_list_reads_no_items()
    {
        var sap = new RecordingServiceLayer { VanSaleRows = "[]" };
        var client = CreateClient(sap);

        Assert.Empty(await client.GetVanSalesApprovedItemsAsync());
        Assert.Empty(sap.ItemReads);
    }

    // ── A missing query object is a fault, not an answer ────────────────────

    /// <summary>
    /// The statement is ensured to exist immediately before it is read, so a 404 means SAP no longer
    /// holds it. Reporting that as "nothing is approved for van sale" is the shape of failure this
    /// codebase keeps having to close: it intersects every warehouse page down to nothing and comes
    /// back HTTP 200, so every van in the fleet reads as empty and there is no error to report.
    /// </summary>
    [Fact]
    public async Task A_missing_approval_query_is_an_error_rather_than_an_empty_approval_list()
    {
        var sap = new RecordingServiceLayer
        {
            VanSaleRows = """[{"ItemCode":"CHE011"}]""",
            VanSaleQueryMissing = true
        };
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetVanSalesApprovedItemCodesAsync());
    }

    /// <summary>
    /// And it has to reach the caller, not be absorbed into an empty page on the way. This is the read
    /// a handset makes to fill its sell screen.
    /// </summary>
    [Fact]
    public async Task A_missing_approval_query_never_reads_as_a_van_carrying_nothing()
    {
        var sap = new RecordingServiceLayer
        {
            WarehouseRows = """[{"ItemCode":"CHE011"},{"ItemCode":"NRI049"}]""",
            VanSaleRows = """[{"ItemCode":"CHE011"}]""",
            VanSaleQueryMissing = true
        };
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetPagedItemsInWarehouseAsync("MSA", page: 1, pageSize: 20, vanSaleOnly: true));
    }

    /// <summary>
    /// The failure is not cached as though it were an answer: the next read tries again rather than
    /// serving an empty catalogue from memory for a quarter of an hour.
    /// </summary>
    [Fact]
    public async Task A_missing_approval_query_is_retried_rather_than_cached()
    {
        var sap = new RecordingServiceLayer
        {
            VanSaleRows = """[{"ItemCode":"CHE011"}]""",
            VanSaleQueryMissing = true
        };
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVanSalesApprovedItemCodesAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVanSalesApprovedItemCodesAsync());

        Assert.Equal(2, sap.ExecutedVanSaleQueries.Count);
    }

    private static SAPServiceLayerClient CreateClient(RecordingServiceLayer sap)
    {
        var httpClient = new HttpClient(sap)
        {
            BaseAddress = new Uri("https://sap.invalid/b1s/v1/")
        };

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
            new StubItemUomMappingStore());
    }

    /// <summary>
    /// Answers the two stored queries these paths use, keyed by the code in the URL, and serves the
    /// item master read from whichever codes the filter named.
    /// </summary>
    private sealed class RecordingServiceLayer : HttpMessageHandler
    {
        public string WarehouseRows { get; init; } = "[]";

        public string VanSaleRows { get; init; } = "[]";

        /// <summary>SAP no longer holds the approval query object the read names.</summary>
        public bool VanSaleQueryMissing { get; init; }

        public List<string> CreatedCodes { get; } = [];

        public List<string> CreatedStatements { get; } = [];

        public List<string> ExecutedVanSaleQueries { get; } = [];

        public List<string> ItemReads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (path.EndsWith("/SQLQueries", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var created = JsonDocument.Parse(body).RootElement;
                CreatedCodes.Add(created.GetProperty("SqlCode").GetString()!);
                CreatedStatements.Add(created.GetProperty("SqlText").GetString()!);
                return Json("{}", HttpStatusCode.Created);
            }

            if (path.EndsWith("/List", StringComparison.Ordinal))
            {
                var isVanSale = path.Contains("VAN_SALE_ITEMS", StringComparison.Ordinal);
                if (isVanSale)
                {
                    ExecutedVanSaleQueries.Add(Uri.UnescapeDataString(uri.Query));

                    if (VanSaleQueryMissing)
                    {
                        return Json("{}", HttpStatusCode.NotFound);
                    }
                }

                // Everything comes back on the first page; a $skip is the caller checking for a second.
                var exhausted = uri.Query.Contains("$skip=", StringComparison.Ordinal);
                var rows = exhausted ? "[]" : isVanSale ? VanSaleRows : WarehouseRows;
                return Json($"{{\"value\":{rows}}}");
            }

            if (path.EndsWith("/Items", StringComparison.Ordinal))
            {
                ItemReads.Add(Uri.UnescapeDataString(uri.Query));
                return Json($"{{\"value\":{BuildItemRows(uri.Query)}}}");
            }

            // The existence probe. Nothing is stored, so every statement is created once.
            return Json("{}", HttpStatusCode.NotFound);
        }

        /// <summary>Echoes back one item row per <c>ItemCode eq '...'</c> the filter asked for.</summary>
        private static string BuildItemRows(string query)
        {
            var codes = Uri.UnescapeDataString(query)
                .Split("ItemCode eq '", StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(part => part[..part.IndexOf('\'')])
                .ToList();

            var rows = codes.Select(code =>
                $$"""{"ItemCode":"{{code}}","ItemName":"{{code}} name","ItemType":"itItems","QuantityOnStock":10.0,"QuantityOrderedByCustomers":0.0}""");

            return $"[{string.Join(",", rows)}]";
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
