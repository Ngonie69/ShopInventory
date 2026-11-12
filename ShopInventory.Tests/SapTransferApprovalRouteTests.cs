using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Features.InventoryTransfers.SapApproval;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Which transfer request conversions go to SAP's own approval: the routes an active SAP transfer
/// template covers, read from its user query, except anything to or from a depot or a van.
/// </summary>
/// <remarks>
/// The queries are the test company's own, read on 2026-11-12 (KEFALOS_TEST_3, 37 transfer templates),
/// spacing and all — <c>$[OWTR.Filler]and</c> and <c>NOT Like'CMFS'</c> are how SAP stores them.
/// </remarks>
public sealed class SapTransferApprovalRouteTests
{
    // Template #19 "Transfer to CMB".
    private const string ToCmb =
        "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\"= $[OWTR.ToWhsCode] and T0.\"ToWhsCode\" = 'CMB'";

    // Template #71 "Transfer To CMFS1".
    private const string ToCmfs =
        "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\"= $[OWTR.ToWhsCode] and T0.\"ToWhsCode\" = 'CMFS'";

    // Template #30 "Transfer From CMB1".
    private const string FromCmb =
        "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"Filler\" = $[OWTR.Filler]and T0.\"Filler\" = 'CMB' and " +
        "T0.\"ToWhsCode\"= $[OWTR.ToWhsCode] and T0.\"ToWhsCode\" NOT Like'CMFS' and T0.\"ToWhsCode\" NOT Like'TRANSIT' and " +
        "T0.\"ToWhsCode\" NOT Like'CMTRANS'";

    // Template #66 "APPROVAL FOR DISPICE": one column equal to two values, so it never holds.
    private const string DispiceAndExport =
        "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\" = $[OWTR.ToWhsCode]and T0.\"ToWhsCode\" = 'DISPICE' and T0.\"ToWhsCode\" = 'EXPHW'";

    // Template #76 "CMFS Transfers DISPA": no document reference, an OR list in brackets.
    private const string FromCmtransToDispatch =
        "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"Filler\" = 'CMTRANS'and (T0.\"ToWhsCode\" = 'DISPCH' OR " +
        "T0.\"ToWhsCode\" = 'DISPYOG' OR T0.\"ToWhsCode\" = 'RETURNS' OR T0.\"ToWhsCode\" = 'SALES')";

    // Template #86 "PROPC".
    private const string ReturnsToPropc =
        "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"Filler\" = 'RETURNS'and (T0.\"Filler\" = 'RETURNS' OR " +
        "T0.\"Filler\" = 'DISPCH' OR T0.\"Filler\" ='PROCS' OR T0.\"Filler\" = 'SALES') and T0.\"ToWhsCode\"= 'PROPC'";

    // ── Reading a condition ─────────────────────────────

    [Theory]
    [InlineData(ToCmb, "TRANSIT", "CMB", true)]
    [InlineData(ToCmb, "CMB", "CMFS", false)]
    [InlineData(ToCmfs, "CMB", "CMFS", true)]
    [InlineData(ToCmfs, "CMFS", "CMB", false)]
    [InlineData(FromCmb, "CMB", "CMH", true)]
    [InlineData(FromCmb, "CMB", "CMFS", false)]
    [InlineData(FromCmb, "CMB", "TRANSIT", false)]
    [InlineData(FromCmb, "CMH", "CMB", false)]
    [InlineData(DispiceAndExport, "DISPCH", "DISPICE", false)]
    [InlineData(DispiceAndExport, "RESWH", "EXPHW", false)]
    [InlineData(FromCmtransToDispatch, "CMTRANS", "DISPYOG", true)]
    [InlineData(FromCmtransToDispatch, "CMTRANS", "CMB", false)]
    [InlineData(FromCmtransToDispatch, "CMFS", "DISPCH", false)]
    [InlineData(ReturnsToPropc, "RETURNS", "PROPC", true)]
    [InlineData(ReturnsToPropc, "DISPCH", "PROPC", false)]
    public void A_template_query_holds_for_the_routes_SAP_would_hold(string query, string from, string to, bool holds)
    {
        var condition = SapTransferTemplateCondition.Parse(query);

        Assert.True(condition.IsReadable, condition.Unreadable);
        Assert.Equal(holds, condition.Matches(from, to));
    }

    [Theory]
    [InlineData("SELECT DISTINCT 'TRUE' FROM DUMMY WHERE $[OWTR.ToWhsCode] = 'WASHBAY'", "DISPCH", "WASHBAY", true)]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\" LIKE 'CM-BAY%'", "CMFC", "CM-BAY 3", true)]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\" LIKE 'CM-BAY%'", "CMFC", "CMFS", false)]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE T0.\"Filler\" IN ('PROYG', N'PROIC') AND NOT T0.\"ToWhsCode\" = 'FSRW'", "PROIC", "DISPICE", true)]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE T0.\"Filler\" IN ('PROYG', N'PROIC') AND NOT T0.\"ToWhsCode\" = 'FSRW'", "PROIC", "FSRW", false)]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE T0.\"Filler\" NOT IN ('PROYG') AND T0.ToWhsCode <> 'FSRW'", "WAREH", "PROYG", true)]
    [InlineData("select distinct 'TRUE' from owtr t0 where t0.\"ToWhsCode\" = 'cmb';", "TRANSIT", "CMB", true)]
    [InlineData("SELECT 'TRUE' FROM OWTR WHERE \"ToWhsCode\" = 'CMB' AND 'TRANSIT' = Filler", "TRANSIT", "CMB", true)]
    public void Other_spellings_of_the_same_test_read_the_same_way(string query, string from, string to, bool holds)
    {
        var condition = SapTransferTemplateCondition.Parse(query);

        Assert.True(condition.IsReadable, condition.Unreadable);
        Assert.Equal(holds, condition.Matches(from, to));
    }

    [Theory]
    [InlineData("")]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE T0.\"DocTotal\" = '100'")]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE T0.\"DocTotal\" > 100")]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 INNER JOIN WTR1 T1 ON T0.\"DocEntry\" = T1.\"DocEntry\" WHERE T1.\"WhsCode\" = 'CMB'")]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE T1.\"ToWhsCode\" = 'CMB'")]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE $[OWTR.Comments] = 'x'")]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE UPPER(T0.\"ToWhsCode\") = 'CMB'")]
    [InlineData("SELECT 'TRUE' FROM OWTR T0 WHERE (T0.\"ToWhsCode\" = 'CMB'")]
    public void A_condition_outside_the_known_shape_is_unreadable_and_holds_nothing(string query)
    {
        var condition = SapTransferTemplateCondition.Parse(query);

        Assert.False(condition.IsReadable);
        Assert.False(string.IsNullOrWhiteSpace(condition.Unreadable));
        Assert.False(condition.Matches("TRANSIT", "CMB"));
    }

    // ── Depots and vans ─────────────────────────────────

    [Theory]
    [InlineData("KEFGRC", "CMB", null)]
    [InlineData("CMB", "KEFBYS", null)]
    [InlineData("CMB", "VAN010", null)]
    [InlineData("CMB", "CMFS", "VAN004")]
    [InlineData("CMB", "CMFS", "CORMACH2")]
    public void A_route_through_a_depot_or_a_van_is_excluded(string from, string to, string? lineWarehouse)
    {
        Assert.NotNull(SapTransferApprovalRoutes.ExcludedWarehouse(from, to, [lineWarehouse]));
    }

    [Fact]
    public void A_route_between_factory_warehouses_is_not_excluded()
    {
        Assert.Null(SapTransferApprovalRoutes.ExcludedWarehouse("CMB", "CMFS", ["CMB", "CMFS", null]));
        // The depots' transit warehouse is the factory's, not a depot.
        Assert.Null(SapTransferApprovalRoutes.ExcludedWarehouse("DISPCH", "DEPOTRAN", []));
    }

    // ── Templates as SAP holds them ─────────────────────

    [Fact]
    public async Task A_request_into_CMFS_is_covered_by_the_active_template_only()
    {
        var sap = new TemplateSap(
            Template(26, "Transfer To CMFS", active: false, queries: [337]),
            Template(71, "Transfer To CMFS1", queries: [337]),
            Template(30, "Transfer From CMB1", queries: [312]))
        {
            Queries = { [337] = ToCmfs, [312] = FromCmb }
        };

        var route = await Routes(sap).ForRequestAsync(Request("CMB", "CMFS"), default);

        Assert.Equal(SapTransferRouteKind.Covered, route.Kind);
        Assert.Equal(["Transfer To CMFS1"], route.Templates.Select(template => template.Name));
        Assert.Equal("'Transfer To CMFS1'", route.TemplateNames);
    }

    [Fact]
    public async Task A_route_no_template_holds_is_not_covered()
    {
        var sap = new TemplateSap(Template(19, "Transfer to CMB", queries: [311])) { Queries = { [311] = ToCmb } };

        var route = await Routes(sap).ForRequestAsync(Request("CMB", "CMFS"), default);

        Assert.Equal(SapTransferRouteKind.NotCovered, route.Kind);
        Assert.Empty(route.Templates);
    }

    [Fact]
    public async Task A_depot_or_van_route_is_decided_without_reading_SAP()
    {
        var route = await Routes(StubProxy.Unused<ISAPServiceLayerClient>()).ForRequestAsync(Request("KEFGRC", "VAN002"), default);

        Assert.Equal(SapTransferRouteKind.Excluded, route.Kind);
        Assert.Equal("KEFGRC", route.ExcludedWarehouse);
    }

    [Fact]
    public async Task A_template_with_no_conditions_holds_every_route()
    {
        // UseTerms tNO is SAP's "Always".
        var sap = new TemplateSap(Template(6, "Stock transfer", useTerms: false));

        var route = await Routes(sap).ForRequestAsync(Request("CMB", "CMFS"), default);

        Assert.True(route.IsCovered);
    }

    [Fact]
    public async Task Templates_on_other_documents_and_plain_terms_hold_nothing()
    {
        var sap = new TemplateSap(
            Template(18, "Transfer Request CMB", useTerms: false, document: "atdtInventoryTransferRequest"),
            Template(99, "Big transfers", terms: 1));

        var route = await Routes(sap).ForRequestAsync(Request("TRANSIT", "CMB"), default);

        Assert.Equal(SapTransferRouteKind.NotCovered, route.Kind);
    }

    [Fact]
    public async Task Two_queries_on_one_template_each_hold_their_own_route()
    {
        // Template #75 "DISPATCH": one query per dispatch warehouse.
        var sap = new TemplateSap(Template(75, "DISPATCH", queries: [656, 659]))
        {
            Queries =
            {
                [656] = "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\"= $[OWTR.ToWhsCode] and T0.\"ToWhsCode\" = 'DISPCH'",
                [659] = "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\"= $[OWTR.ToWhsCode] and T0.\"ToWhsCode\" = 'DISPYOG'"
            }
        };
        var routes = Routes(sap);

        Assert.True((await routes.ForRequestAsync(Request("FSRW", "DISPYOG"), default)).IsCovered);
        Assert.True((await routes.ForRequestAsync(Request("FSRW", "DISPCH"), default)).IsCovered);
        Assert.False((await routes.ForRequestAsync(Request("FSRW", "DISPICE"), default)).IsCovered);
    }

    [Fact]
    public async Task Templates_are_read_once_and_kept()
    {
        var sap = new TemplateSap(Template(19, "Transfer to CMB", queries: [311])) { Queries = { [311] = ToCmb } };
        var routes = Routes(sap);

        await routes.ForRequestAsync(Request("TRANSIT", "CMB"), default);
        await routes.ForRequestAsync(Request("CMTRANS", "CMB"), default);

        Assert.Equal(1, sap.TemplateReads);
    }

    [Fact]
    public async Task A_conversion_refuses_to_guess_when_SAP_templates_were_never_read()
    {
        var sap = new TemplateSap { Failure = new HttpRequestException("SAP is down") };

        await Assert.ThrowsAsync<HttpRequestException>(() => Routes(sap).ForRequestAsync(Request("CMB", "CMFS"), default));
        Assert.Null(await Routes(sap).TryForRouteAsync("CMB", "CMFS", [], default));
    }

    [Fact]
    public async Task A_failed_refresh_uses_the_last_good_read()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sap = new TemplateSap(Template(71, "Transfer To CMFS1", queries: [337])) { Queries = { [337] = ToCmfs } };
        await Routes(sap, cache).ForRequestAsync(Request("CMB", "CMFS"), default);

        cache.Remove("sap-transfer-approval-templates");
        sap.Failure = new HttpRequestException("SAP is down");

        Assert.True((await Routes(sap, cache).ForRequestAsync(Request("CMB", "CMFS"), default)).IsCovered);
    }

    // ── Helpers ─────────────────────────────────────────

    private static SapTransferApprovalRoutes Routes(ISAPServiceLayerClient sap, IMemoryCache? cache = null)
        => new(sap, cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger<SapTransferApprovalRoutes>.Instance);

    private static SapTransferApprovalRoutes Routes(TemplateSap sap, IMemoryCache? cache = null) => Routes(sap.AsClient(), cache);

    private static InventoryTransferRequest Request(string from, string to) => new()
    {
        DocEntry = 1,
        DocNum = 1,
        FromWarehouse = from,
        ToWarehouse = to,
        StockTransferLines = [new InventoryTransferRequestLine { ItemCode = "ICA004", Quantity = 1, FromWarehouseCode = from, WarehouseCode = to }]
    };

    private static SAPApprovalTemplateCondition Template(
        int code,
        string name,
        bool active = true,
        bool useTerms = true,
        string document = "atdtInventoryTransfer",
        int[]? queries = null,
        int terms = 0) => new()
        {
            Code = code,
            Name = name,
            IsActive = active ? "tYES" : "tNO",
            UseTerms = useTerms ? "tYES" : "tNO",
            ApprovalTemplateDocuments = [new SAPApprovalTemplateDocument { DocumentType = document }],
            ApprovalTemplateQueries = (queries ?? []).Select(id => new SAPApprovalTemplateQuery { QueryID = id }).ToList(),
            ApprovalTemplateTerms = Enumerable.Range(0, terms).Select(_ => System.Text.Json.JsonDocument.Parse("{}").RootElement).ToList()
        };

    private sealed class TemplateSap(params SAPApprovalTemplateCondition[] templates)
    {
        public Dictionary<int, string> Queries { get; } = [];
        public Exception? Failure { get; set; }
        public int TemplateReads { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetApprovalTemplateConditionsAsync) => ReadTemplates(),
                nameof(ISAPServiceLayerClient.GetUserQueriesAsync) => Task.FromResult(
                    ((IReadOnlyCollection<int>)args![0]!)
                        .Where(Queries.ContainsKey)
                        .Select(key => new SAPUserQuery { InternalKey = key, QueryCategory = 19, Query = Queries[key] })
                        .ToList()),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<List<SAPApprovalTemplateCondition>> ReadTemplates()
        {
            TemplateReads++;
            return Failure is null
                ? Task.FromResult(templates.ToList())
                : Task.FromException<List<SAPApprovalTemplateCondition>>(Failure);
        }
    }
}
