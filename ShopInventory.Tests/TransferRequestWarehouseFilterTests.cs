using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Web.Features.InventoryTransfers;
using WebWarehouse = ShopInventory.Web.Models.WarehouseDto;

namespace ShopInventory.Tests;

/// <summary>
/// The From and To warehouse filters on /inventory-transfers.
/// </summary>
/// <remarks>
/// Both lists were built from the codes on the loaded page of a hundred requests, and both sifted
/// only that page, so a warehouse whose requests were older than the newest hundred could not be
/// chosen and could not be found. Administrators and the wash bay act on any warehouse; they now
/// get every warehouse, and a choice is asked of SAP rather than looked for on the page.
/// </remarks>
[Collection("SapServiceLayerClient")]
public class TransferRequestWarehouseFilterTests
{
    // ── The SAP read ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_source_warehouse_is_asked_of_SAP_alongside_the_document_status()
    {
        var sap = new RecordingSap();

        await CreateClient(sap).GetPagedInventoryTransferRequestsAsync(1, 100, "bost_Open", fromWarehouse: "DISPCH");

        Assert.Equal("DocumentStatus eq 'bost_Open' and FromWarehouse eq 'DISPCH'", FilterOf(sap.Requests.Single()));
    }

    [Fact]
    public async Task Both_ends_are_asked_of_SAP_and_a_code_with_a_space_survives()
    {
        var sap = new RecordingSap();

        await CreateClient(sap).GetPagedInventoryTransferRequestsAsync(
            2, 100, documentStatus: null, fromWarehouse: "PROYG 2", toWarehouse: "FSRW");

        var url = sap.Requests.Single();
        Assert.Equal("FromWarehouse eq 'PROYG 2' and ToWarehouse eq 'FSRW'", FilterOf(url));
        Assert.Contains("$skip=100", url, StringComparison.Ordinal);
        Assert.Contains("$orderby=DocEntry desc", Uri.UnescapeDataString(url), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_status_and_no_warehouse_sends_no_filter()
    {
        var sap = new RecordingSap();

        await CreateClient(sap).GetPagedInventoryTransferRequestsAsync(1, 100);

        Assert.DoesNotContain("$filter", sap.Requests.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_handler_passes_the_warehouses_through_and_drops_blank_ones()
    {
        object?[]? seen = null;
        var sapClient = StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            if (method.Name != nameof(ISAPServiceLayerClient.GetPagedInventoryTransferRequestsAsync))
                return null;
            seen = args;
            return Task.FromResult(new List<InventoryTransferRequest>());
        });
        var approvals = StubProxy.For<IInventoryTransferApprovalService>((method, _) =>
            method.Name == nameof(IInventoryTransferApprovalService.EnrichAsync) ? Task.CompletedTask : null);
        var handler = new GetPagedTransferRequestsHandler(
            sapClient, approvals, Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetPagedTransferRequestsHandler>.Instance);

        var result = await handler.Handle(
            new GetPagedTransferRequestsQuery(1, 100, "open", " DISPCH ", "  "), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(seen);
        Assert.Equal("bost_Open", seen![2]);
        Assert.Equal("DISPCH", seen[3]);
        Assert.Null(seen[4]);
    }

    [Theory]
    [InlineData("DISPCH' or 1 eq 1")]
    [InlineData("DISPCH;")]
    [InlineData("DISP--CH")]
    public void A_warehouse_that_could_end_the_OData_literal_is_refused(string code)
    {
        var validator = new GetPagedTransferRequestsValidator();

        Assert.False(validator.Validate(new GetPagedTransferRequestsQuery(1, 100, FromWarehouse: code)).IsValid);
        Assert.False(validator.Validate(new GetPagedTransferRequestsQuery(1, 100, ToWarehouse: code)).IsValid);
    }

    [Fact]
    public void Real_warehouse_codes_pass_validation()
    {
        var validator = new GetPagedTransferRequestsValidator();

        Assert.True(validator.Validate(new GetPagedTransferRequestsQuery(1, 100, null, "PROYG 2", "CM-BAY 1")).IsValid);
        Assert.True(validator.Validate(new GetPagedTransferRequestsQuery(1, 100)).IsValid);
    }

    // ── The filter rows ──────────────────────────────────────────────────────

    private static readonly WebWarehouse[] Warehouses =
    [
        new() { WarehouseCode = "DISPYOG", WarehouseName = "Dispatch Yoghurt" },
        new() { WarehouseCode = "WASHBAY", WarehouseName = "Wash Bay" },
        new() { WarehouseCode = "DISPCH", WarehouseName = "Dispatch Cheese" },
        new() { WarehouseCode = "CORMACH", WarehouseName = "Cortina Machipisa Centre" },
        new() { WarehouseCode = "TRANSIT", WarehouseName = "TRANSIT" }
    ];

    // One entry per loaded request: three from DISPYOG, one from DISPCH, one from a code the
    // warehouse cache does not hold.
    private static readonly string?[] SourcesOnPage = ["DISPYOG", "DISPCH", "dispyog", null, "DISPYOG", "OLDCODE"];

    [Fact]
    public void Everyone_else_keeps_the_codes_on_the_page()
    {
        var rows = WarehouseFilterOptions.Build("Any source", SourcesOnPage, Warehouses, everyWarehouse: false);

        Assert.Equal(["", "DISPCH", "DISPYOG", "OLDCODE"], rows.Select(row => row.Value));
        Assert.All(rows, row => Assert.Null(row.Group));
        Assert.All(rows.Skip(1), row => Assert.Null(row.Hint));
    }

    [Fact]
    public void Every_warehouse_lists_the_page_first_with_counts_then_the_rest_A_to_Z()
    {
        var rows = WarehouseFilterOptions.Build("Any source", SourcesOnPage, Warehouses, everyWarehouse: true);

        Assert.True(rows[0].IsUnset);
        Assert.Equal("Any source", rows[0].Label);

        var onPage = rows.Where(row => row.Group == WarehouseFilterOptions.OnThisPageGroup).ToList();
        Assert.Equal(["DISPCH", "DISPYOG", "OLDCODE"], onPage.Select(row => row.Value));
        Assert.Equal(["1", "3", "1"], onPage.Select(row => row.Hint));
        Assert.Equal(["Dispatch Cheese", "Dispatch Yoghurt", null], onPage.Select(row => row.Detail));

        var others = rows.Where(row => row.Group == WarehouseFilterOptions.OtherWarehousesGroup).ToList();
        Assert.Equal(["CORMACH", "TRANSIT", "WASHBAY"], others.Select(row => row.Value));
        Assert.All(others, row => Assert.Null(row.Hint));

        // Each group's rows sit together, or the menu would head the same group twice.
        Assert.Equal(rows.Count, 1 + onPage.Count + others.Count);
        Assert.Equal(
            [null, .. onPage.Select(_ => WarehouseFilterOptions.OnThisPageGroup), .. others.Select(_ => WarehouseFilterOptions.OtherWarehousesGroup)],
            rows.Select(row => row.Group));
    }

    [Fact]
    public void A_name_that_only_repeats_the_code_is_not_shown_twice()
    {
        var rows = WarehouseFilterOptions.Build("Any source", [], Warehouses, everyWarehouse: true);

        Assert.Null(rows.Single(row => row.Value == "TRANSIT").Detail);
        Assert.Equal("Wash Bay", rows.Single(row => row.Value == "WASHBAY").Detail);
    }

    [Fact]
    public void A_chosen_warehouse_nothing_else_names_stays_choosable()
    {
        // SAP answered with nothing for it and the warehouse cache has lost it: without this row
        // the trigger would read "Any source" while the page is still filtered by it.
        var rows = WarehouseFilterOptions.Build("Any source", [], Warehouses, everyWarehouse: true, selected: "GONE01");

        var kept = rows.Single(row => row.Value == "GONE01");
        Assert.Equal("0", kept.Hint);
    }

    [Fact]
    public void A_chosen_warehouse_off_the_page_is_not_duplicated()
    {
        var rows = WarehouseFilterOptions.Build("Any source", SourcesOnPage, Warehouses, everyWarehouse: true, selected: "WASHBAY");

        Assert.Single(rows, row => row.Value == "WASHBAY");
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static string FilterOf(string url)
    {
        var query = new Uri("https://sap.invalid" + url).Query.TrimStart('?');
        var filter = query.Split('&').Single(part => part.StartsWith("$filter=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(filter["$filter=".Length..]);
    }

    private static SAPServiceLayerClient CreateClient(HttpMessageHandler sap)
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

    /// <summary>Answers the login and records every transfer request read, returning no rows.</summary>
    private sealed class RecordingSap : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;
            if (target.EndsWith("/Login", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));

            Requests.Add(target);
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
