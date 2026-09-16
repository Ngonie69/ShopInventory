using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Web.Services;
using ApiValues = ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales.DesktopSalesFilterValues;
using WebValues = ShopInventory.Web.Services.DesktopSalesFilterValues;

namespace ShopInventory.Tests;

/// <summary>
/// The filter panel behind /desktop-sales: chips that take several values at once, an amount window, a
/// tender comparison, a sort and the counts the chips carry.
///
/// Every one of these is asked of the real handler and then read back through the Web's own
/// hand-mirrored models, the way <c>ListThroughTheWebAsync</c> does on the vending list — the console is
/// the only caller that uses any of it, and a facet the two sides disagree about would reach the page as
/// a panel full of zeroes rather than as an error.
/// </summary>
public sealed class DesktopSalesFilterSurfaceTests : IDisposable
{
    private const string Shop = "KEFSHOP";
    private const string Depot = "KEFGRC";
    private const string Bulawayo = "KEFBYO";

    private static readonly DateTime Today = new(2026, 9, 16);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private int _reference;

    public DesktopSalesFilterSurfaceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Several values at once ─────────────────────────────────────────────

    [Fact]
    public async Task Naming_two_warehouses_lists_both_and_nothing_else()
    {
        Sell(warehouse: Shop);       // SALE-1
        Sell(warehouse: Depot);      // SALE-2
        Sell(warehouse: Bulawayo);   // SALE-3
        await _context.SaveChangesAsync();

        var found = await ListAsync(q => q with { Warehouses = [Shop, Bulawayo] });

        Assert.Equal(["SALE-1", "SALE-3"], Refs(found));
        Assert.Equal(2, found.TotalCount);
    }

    [Fact]
    public async Task The_singular_warehouse_and_the_plural_are_one_filter()
    {
        Sell(warehouse: Shop);       // SALE-1
        Sell(warehouse: Depot);      // SALE-2
        Sell(warehouse: Bulawayo);   // SALE-3
        await _context.SaveChangesAsync();

        // A caller that sends both forms means both warehouses, not neither and not only the last one.
        var found = await ListAsync(q => q with { WarehouseCode = Shop, Warehouses = [Depot] });

        Assert.Equal(["SALE-1", "SALE-2"], Refs(found));
    }

    [Fact]
    public async Task Naming_two_consolidation_states_lists_both()
    {
        Sell(consolidation: DesktopSaleConsolidationStatus.Pending);       // SALE-1
        Sell(consolidation: DesktopSaleConsolidationStatus.Consolidated);  // SALE-2
        Sell(consolidation: DesktopSaleConsolidationStatus.Failed);        // SALE-3
        Sell(consolidation: DesktopSaleConsolidationStatus.Excluded);      // SALE-4
        await _context.SaveChangesAsync();

        var found = await ListAsync(q => q with { ConsolidationStatuses = ["Failed", "Excluded"] });

        Assert.Equal(["SALE-3", "SALE-4"], Refs(found));
    }

    [Fact]
    public async Task A_status_nobody_defined_is_dropped_rather_than_refusing_the_whole_filter()
    {
        Sell(consolidation: DesktopSaleConsolidationStatus.Failed);        // SALE-1
        Sell(consolidation: DesktopSaleConsolidationStatus.Consolidated);  // SALE-2
        await _context.SaveChangesAsync();

        // A console one release ahead of this API must not have its whole filter refused over one chip.
        var found = await ListAsync(q => q with { ConsolidationStatuses = ["Failed", "Abandoned"] });

        Assert.Equal(["SALE-1"], Refs(found));
    }

    [Fact]
    public async Task Fiscal_result_and_payment_method_are_filters_of_their_own()
    {
        Sell(fiscal: DesktopSaleFiscalizationStatus.Success, tender: TenderTypes.Cash);    // SALE-1
        Sell(fiscal: DesktopSaleFiscalizationStatus.Failed, tender: TenderTypes.Cash);     // SALE-2
        Sell(fiscal: DesktopSaleFiscalizationStatus.Failed, tender: TenderTypes.Swipe);    // SALE-3
        Sell(fiscal: DesktopSaleFiscalizationStatus.Pending, tender: TenderTypes.Ecocash); // SALE-4
        await _context.SaveChangesAsync();

        Assert.Equal(
            ["SALE-2", "SALE-3", "SALE-4"],
            Refs(await ListAsync(q => q with { FiscalizationStatuses = ["Failed", "Pending"] })));

        Assert.Equal(
            ["SALE-3", "SALE-4"],
            Refs(await ListAsync(q => q with { PaymentMethods = [TenderTypes.Swipe, TenderTypes.Ecocash] })));

        // Two groups at once narrow each other; they do not add up.
        Assert.Equal(
            ["SALE-3"],
            Refs(await ListAsync(q => q with
            {
                FiscalizationStatuses = ["Failed"],
                PaymentMethods = [TenderTypes.Swipe]
            })));
    }

    [Fact]
    public async Task Naming_channels_turns_off_the_default_scope_exactly_as_the_singular_did()
    {
        Sell(source: SaleSourceSystems.ShopTill);         // SALE-1
        Sell(source: SaleSourceSystems.Vending);          // SALE-2
        Sell(source: SaleSourceSystems.VanSales);         // SALE-3
        Sell(source: SaleSourceSystems.VanSalesOnline);   // SALE-4 — the receipt carrier
        await _context.SaveChangesAsync();

        // Naming none is the default scope: everything but the online van receipt carriers.
        Assert.Equal(["SALE-1", "SALE-2", "SALE-3"], Refs(await ListAsync(q => q)));

        Assert.Equal(
            ["SALE-1", "SALE-2"],
            Refs(await ListAsync(q => q with
            {
                SourceSystems = [SaleSourceSystems.ShopTill, SaleSourceSystems.Vending]
            })));

        // And naming the carriers still reaches them, so an operator chasing a fiscal reference can.
        Assert.Equal(
            ["SALE-4"],
            Refs(await ListAsync(q => q with { SourceSystems = [SaleSourceSystems.VanSalesOnline] })));
    }

    // ── The amount window and the tender comparison ────────────────────────

    [Fact]
    public async Task The_amount_window_is_inclusive_at_both_ends()
    {
        Sell(total: 5m);    // SALE-1
        Sell(total: 10m);   // SALE-2
        Sell(total: 25m);   // SALE-3
        Sell(total: 40m);   // SALE-4
        await _context.SaveChangesAsync();

        Assert.Equal(
            ["SALE-2", "SALE-3"],
            Refs(await ListAsync(q => q with { MinTotal = 10m, MaxTotal = 25m })));

        Assert.Equal(
            ["SALE-3", "SALE-4"],
            Refs(await ListAsync(q => q with { MinTotal = 25m })));

        Assert.Equal(
            ["SALE-1", "SALE-2"],
            Refs(await ListAsync(q => q with { MaxTotal = 10m })));
    }

    [Fact]
    public async Task The_tender_comparison_separates_change_given_from_money_the_day_is_short()
    {
        Sell(total: 10m, paid: 10m);      // SALE-1 — exact
        Sell(total: 12.40m, paid: 15m);   // SALE-2 — cash rounded up, change given
        Sell(total: 20m, paid: 18m);      // SALE-3 — the drawer took less than the invoice says
        await _context.SaveChangesAsync();

        Assert.Equal(
            ["SALE-1"],
            Refs(await ListAsync(q => q with { PaymentDifference = DesktopSalesPaymentDifferences.Exact })));

        Assert.Equal(
            ["SALE-2"],
            Refs(await ListAsync(q => q with { PaymentDifference = DesktopSalesPaymentDifferences.Over })));

        Assert.Equal(
            ["SALE-3"],
            Refs(await ListAsync(q => q with { PaymentDifference = DesktopSalesPaymentDifferences.Under })));

        Assert.Equal(
            ["SALE-1", "SALE-2", "SALE-3"],
            Refs(await ListAsync(q => q with { PaymentDifference = DesktopSalesPaymentDifferences.Any })));
    }

    // ── Sorting ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Total_high_to_low_names_the_largest_sale_and_not_the_largest_on_page_one()
    {
        Sell(total: 5m, at: Today.AddHours(9));     // SALE-1
        Sell(total: 40m, at: Today.AddHours(10));   // SALE-2
        Sell(total: 25m, at: Today.AddHours(11));   // SALE-3
        await _context.SaveChangesAsync();

        // One row per page, so an implementation that sorted the page it was handed would answer with
        // the newest sale rather than the largest.
        var page = await ListAsync(q => q with
        {
            Sort = DesktopSalesSortOrders.TotalDescending,
            PageSize = 1
        });

        Assert.Equal(["SALE-2"], InOrder(page));
        Assert.Equal(3, page.TotalCount);
        Assert.True(page.HasMore);
    }

    [Theory]
    [InlineData(DesktopSalesSortOrders.Newest, "SALE-3,SALE-2,SALE-1")]
    [InlineData(DesktopSalesSortOrders.Oldest, "SALE-1,SALE-2,SALE-3")]
    [InlineData(DesktopSalesSortOrders.TotalAscending, "SALE-1,SALE-3,SALE-2")]
    [InlineData(DesktopSalesSortOrders.TotalDescending, "SALE-2,SALE-3,SALE-1")]
    [InlineData("nonsense", "SALE-3,SALE-2,SALE-1")]
    public async Task Every_order_is_the_one_its_name_promises(string sort, string expected)
    {
        Sell(total: 5m, at: Today.AddHours(9));     // SALE-1
        Sell(total: 40m, at: Today.AddHours(10));   // SALE-2
        Sell(total: 25m, at: Today.AddHours(11));   // SALE-3
        await _context.SaveChangesAsync();

        Assert.Equal(expected.Split(','), InOrder(await ListAsync(q => q with { Sort = sort })));
    }

    [Fact]
    public async Task Customer_order_reads_by_the_name_the_page_shows_and_falls_back_to_the_code()
    {
        Sell(cardCode: "RET044", cardName: "Zuze Retail");     // SALE-1
        Sell(cardCode: "COR007", cardName: null);              // SALE-2 — no name, so the code stands
        Sell(cardCode: "WHL003", cardName: "Anchor Wholesale");// SALE-3
        await _context.SaveChangesAsync();

        // "Anchor Wholesale", then "COR007" standing in for a sale with no name, then "Zuze Retail".
        Assert.Equal(
            ["SALE-3", "SALE-2", "SALE-1"],
            InOrder(await ListAsync(q => q with { Sort = DesktopSalesSortOrders.Customer })));
    }

    // ── The counts the chips carry ─────────────────────────────────────────

    [Fact]
    public async Task Facets_are_off_until_a_caller_says_it_will_draw_them()
    {
        Sell();
        await _context.SaveChangesAsync();

        var quiet = await ListAsync(q => q with { IncludeFacets = false });

        Assert.Empty(quiet.Facets?.Consolidation ?? []);
        Assert.Empty(quiet.Facets?.Warehouse ?? []);

        // And the denominator falls back to the total rather than to nothing, so a console that reads it
        // without asking for facets cannot render "filtered from 0".
        Assert.Equal(quiet.TotalCount, quiet.UnfilteredCount);
    }

    [Fact]
    public async Task A_group_counts_itself_with_its_own_selection_lifted()
    {
        Sell(consolidation: DesktopSaleConsolidationStatus.Pending);
        Sell(consolidation: DesktopSaleConsolidationStatus.Pending);
        Sell(consolidation: DesktopSaleConsolidationStatus.Consolidated);
        Sell(consolidation: DesktopSaleConsolidationStatus.Failed);
        await _context.SaveChangesAsync();

        var found = await ListAsync(q => q with
        {
            ConsolidationStatuses = ["Failed"],
            IncludeFacets = true
        });

        // The list is the one failed sale…
        Assert.Equal(1, found.TotalCount);

        // …and the chips still say what pressing each of them would give, which is the only count that is
        // any use on the control that sets it.
        Assert.Equal(2, Count(found.Facets!.Consolidation, "Pending"));
        Assert.Equal(1, Count(found.Facets!.Consolidation, "Consolidated"));
        Assert.Equal(1, Count(found.Facets!.Consolidation, "Failed"));
    }

    [Fact]
    public async Task A_group_is_still_counted_against_every_other_filter()
    {
        Sell(warehouse: Shop, tender: TenderTypes.Cash);
        Sell(warehouse: Shop, tender: TenderTypes.Swipe);
        Sell(warehouse: Depot, tender: TenderTypes.Swipe);
        Sell(warehouse: Depot, tender: TenderTypes.Swipe);
        await _context.SaveChangesAsync();

        var found = await ListAsync(q => q with { Warehouses = [Shop], IncludeFacets = true });

        // Tenders counted within the shop the operator is looking at, not across the company.
        Assert.Equal(1, Count(found.Facets!.PaymentMethod, TenderTypes.Cash));
        Assert.Equal(1, Count(found.Facets!.PaymentMethod, TenderTypes.Swipe));

        // The warehouse group lifts only itself, so the other shop is still countable and still offered.
        Assert.Equal(2, Count(found.Facets!.Warehouse, Shop));
        Assert.Equal(2, Count(found.Facets!.Warehouse, Depot));
    }

    [Fact]
    public void The_two_sides_spell_the_blank_the_same_way()
    {
        // The Web has no reference to the API's project and writes this word out for itself, the way it
        // hand-mirrors every DTO. If the two ever disagree, the blank chip silently stops filtering —
        // it is drawn from the facet, sent straight back, and matched by nothing.
        Assert.Equal(ApiValues.Blank, WebValues.Blank);
    }

    [Fact]
    public async Task A_sale_whose_till_recorded_no_tender_is_still_reachable_from_the_panel()
    {
        Sell(tender: TenderTypes.Cash);   // SALE-1
        Sell(tender: null);               // SALE-2
        Sell(tender: "  ");               // SALE-3
        await _context.SaveChangesAsync();

        var found = await ListAsync(q => q with { IncludeFacets = true });

        // Null and blank are one thing to a reader and are added together, under the reserved word
        // rather than under the empty string — which a query string drops, so a chip built on it would
        // have counted towards the badge and narrowed nothing.
        Assert.Equal(2, Count(found.Facets!.PaymentMethod, ApiValues.Blank));

        // And pressing that chip has to actually reach those two rows.
        Assert.Equal(
            ["SALE-2", "SALE-3"],
            Refs(await ListAsync(q => q with { PaymentMethods = [ApiValues.Blank] })));

        // Beside a real tender it widens rather than replacing.
        Assert.Equal(
            ["SALE-1", "SALE-2", "SALE-3"],
            Refs(await ListAsync(q => q with
            {
                PaymentMethods = [ApiValues.Blank, TenderTypes.Cash]
            })));
    }

    [Fact]
    public async Task A_row_with_no_source_is_reachable_the_same_way_and_does_not_widen_the_default_scope()
    {
        Sell(source: SaleSourceSystems.ShopTill);         // SALE-1
        Sell(source: "");                                 // SALE-2 — written before sources were named
        Sell(source: SaleSourceSystems.VanSalesOnline);   // SALE-3 — the receipt carrier
        await _context.SaveChangesAsync();

        var found = await ListAsync(q => q with { IncludeFacets = true });
        Assert.Equal(1, Count(found.Facets!.SourceSystem, ApiValues.Blank));

        Assert.Equal(
            ["SALE-2"],
            Refs(await ListAsync(q => q with { SourceSystems = [ApiValues.Blank] })));

        // Naming the blank is still naming a channel, so it must not drag the carriers back in.
        Assert.Equal(
            ["SALE-1", "SALE-2"],
            Refs(await ListAsync(q => q with
            {
                SourceSystems = [ApiValues.Blank, SaleSourceSystems.ShopTill]
            })));
    }

    [Fact]
    public async Task The_denominator_is_the_period_before_the_panel_narrowed_it()
    {
        Sell(consolidation: DesktopSaleConsolidationStatus.Pending);
        Sell(consolidation: DesktopSaleConsolidationStatus.Pending);
        Sell(consolidation: DesktopSaleConsolidationStatus.Failed);
        Sell(at: Today.AddDays(-9), day: Today.AddDays(-9));   // outside the period below
        await _context.SaveChangesAsync();

        var found = await ListAsync(q => q with
        {
            FromDate = Today.AddDays(-6),
            ToDate = Today,
            ConsolidationStatuses = ["Failed"],
            IncludeFacets = true
        });

        Assert.Equal(1, found.TotalCount);

        // Three, not four: the period is what the operator chose to look at, so the denominator keeps it
        // and drops only the filters the panel set.
        Assert.Equal(3, found.UnfilteredCount);
    }

    // ── Whose money a caller may read ──────────────────────────────────────

    [Fact]
    public async Task A_shop_scoped_caller_naming_another_shop_is_refused_rather_than_quietly_intersected()
    {
        Sell(warehouse: Shop);
        Sell(warehouse: Bulawayo);
        await _context.SaveChangesAsync();

        var till = await TillAsync(Shop);

        var refused = await HandleAsync(new GetDesktopSalesQuery(till, Warehouses: [Shop, Bulawayo]));

        Assert.True(refused.IsError);
    }

    [Fact]
    public async Task A_shop_scoped_caller_naming_nothing_is_still_confined_facets_included()
    {
        Sell(warehouse: Shop);
        Sell(warehouse: Bulawayo);
        Sell(warehouse: Bulawayo);
        await _context.SaveChangesAsync();

        var till = await TillAsync(Shop);

        var found = await ListAsync(_ => new GetDesktopSalesQuery(till, IncludeFacets: true));

        Assert.Equal(1, found.TotalCount);
        Assert.Equal(1, found.UnfilteredCount);

        // The warehouse facet lifts the request's warehouse filter, not the caller's scope — so this must
        // not be the moment another shop's takings become countable.
        Assert.Equal(1, Count(found.Facets!.Warehouse, Shop));
        Assert.Equal(0, Count(found.Facets!.Warehouse, Bulawayo));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>The references that came back, sorted — for the filters, where the set is the question.</summary>
    private static string[] Refs(DesktopSalesListResponse found) =>
        InOrder(found).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The references in the order the query put them, which is what the sorts are about.</summary>
    private static string[] InOrder(DesktopSalesListResponse found) =>
        found.Sales.Select(sale => sale.ExternalReferenceId).ToArray();

    private static int Count(IEnumerable<DesktopSalesFacetDto> facets, string value) =>
        facets.FirstOrDefault(facet => facet.Value == value)?.Count ?? 0;

    private void Sell(
        decimal total = 10m,
        decimal? paid = null,
        string warehouse = Shop,
        string cardCode = "CIS006",
        string? cardName = "Kefalos Cheese",
        string source = SaleSourceSystems.ShopTill,
        string? tender = TenderTypes.Cash,
        DesktopSaleFiscalizationStatus fiscal = DesktopSaleFiscalizationStatus.Success,
        DesktopSaleConsolidationStatus consolidation = DesktopSaleConsolidationStatus.Pending,
        DateTime? at = null,
        DateTime? day = null)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"SALE-{++_reference}",
            SourceSystem = source,
            CardCode = cardCode,
            CardName = cardName,
            WarehouseCode = warehouse,
            DocDate = (day ?? Today).Date,
            TotalAmount = total,
            VatAmount = Math.Round(total * 0.13m, 2),
            AmountPaid = paid ?? total,
            Currency = "USD",
            PaymentMethod = tender,
            FiscalizationStatus = fiscal,
            ConsolidationStatus = consolidation,
            CreatedBy = "cashier",
            CreatedAt = DateTime.SpecifyKind(at ?? Today.AddHours(8 + _reference), DateTimeKind.Utc),
        });
    }

    /// <summary>A Cashier with no shop, which reads across every warehouse as the console's users do.</summary>
    private Task<Guid> ConsoleAsync() => AccountAsync(ApplicationRoles.Cashier, shopWarehouse: null);

    /// <summary>A Cashier pointed at one shop, which is what confines it to that shop's takings.</summary>
    private Task<Guid> TillAsync(string warehouse) => AccountAsync(ApplicationRoles.Cashier, warehouse);

    private async Task<Guid> AccountAsync(string role, string? shopWarehouse)
    {
        var id = Guid.NewGuid();
        int? shopId = null;

        if (shopWarehouse is not null)
        {
            var shop = new ShopEntity
            {
                Code = shopWarehouse,
                Name = shopWarehouse,
                BusinessPartnerCode = $"{shopWarehouse}-BP",
                WarehouseCode = shopWarehouse,
                IsActive = true,
            };
            _context.Shops.Add(shop);
            await _context.SaveChangesAsync();
            shopId = shop.Id;
        }

        _context.Users.Add(new User
        {
            Id = id,
            Username = $"user{id:N}"[..12],
            PasswordHash = "x",
            Role = role,
            IsActive = true,
            ShopId = shopId,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private async Task<DesktopSalesListResponse> ListAsync(
        Func<GetDesktopSalesQuery, GetDesktopSalesQuery> shape)
    {
        var result = await HandleAsync(shape(new GetDesktopSalesQuery(await ConsoleAsync())));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);

        // Back through the Web's own models, so a facet the two sides spell differently fails here rather
        // than reading as an empty filter panel on the page.
        return ThroughTheWire<DesktopSalesListResult, DesktopSalesListResponse>(result.Value);
    }

    private async Task<ErrorOr.ErrorOr<DesktopSalesListResult>> HandleAsync(GetDesktopSalesQuery query)
    {
        _context.ChangeTracker.Clear();
        return await new GetDesktopSalesHandler(
                _context, new RecordingAuditService(), Options.Create(new FiscalisationSettings()), Microsoft.Extensions.Options.Options.Create(new ShopInventory.Configuration.DesktopSalePostingSettings()), Microsoft.Extensions.Options.Options.Create(new ShopInventory.Configuration.VanSalesPostingSettings()))
            .Handle(query, CancellationToken.None);
    }

    /// <summary>What the Web's HttpClient does to the API's answer: web JSON out, web JSON in.</summary>
    private static TModel ThroughTheWire<TDto, TModel>(TDto dto)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.Deserialize<TModel>(JsonSerializer.Serialize(dto, options), options)!;
    }
}
