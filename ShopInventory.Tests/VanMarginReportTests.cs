using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanMarginReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the local half of the margin report.
///
/// The report is named for a figure it does not yet carry, which makes its failure mode unusual: the
/// danger is not a wrong margin but a plausible-looking one. Every margin field is null and has to
/// stay null — a zero would report an item sold at exactly cost, which is a finding rather than an
/// absence, and it would be indistinguishable from the real thing once the cost source is connected.
///
/// This report has three ways to state a margin that is wrong while looking right, and every one of
/// them flatters.
///
/// It can cost a sale SAP never saw. A line whose document did not post carries no cost, and
/// counting it would put revenue over a cost of nothing. Both halves of the union post by different
/// routes on differently-named columns, so each is pinned separately.
///
/// It can subtract two kinds of money. SAP states a line's cost in the company's local currency
/// while the revenue is in the document's, and this company bills in two — so a margin is only ever
/// stated for a currency matching the cost currency, and a currency that does not match gets no
/// margin row at all rather than one holding a null.
///
/// It can treat an unvalued item as pure profit. B1 leaves the cost column at zero on a line whose
/// item has no valuation yet, and carrying that through would report the item as all margin.
/// </summary>
public sealed class VanMarginReportTests : IDisposable
{
    private const string VanAccount = "VAN010";
    private const string VanWarehouse = "VAN010";

    private static readonly Guid Rep = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static readonly DateTime From = new(2026, 8, 1);
    private static readonly DateTime To = new(2026, 8, 31);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanMarginReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van010",
            Email = "van010@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = VanWarehouse,
            AssignedBusinessPartnerCode = VanAccount
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The absent half ---

    /// <summary>
    /// The ordinary case: a posted sale, a cost for its item, and a margin that is revenue less
    /// cost in the one currency both are stated in.
    /// </summary>
    [Fact]
    public async Task A_posted_sale_with_a_cost_gets_a_margin()
    {
        AddSale(Rep, "S1", 100m, new DateTime(2026, 8, 4), posted: true);
        await _context.SaveChangesAsync();

        var report = await RunAsync(unitCosts: new() { ["CHE011"] = 60m });

        Assert.True(report.Summary.MarginAvailable);
        Assert.Equal("USD", report.Summary.CostCurrency);

        var margin = Assert.Single(report.Summary.MarginByCurrency);
        Assert.Equal("USD", margin.Currency);
        Assert.Equal(100m, margin.Revenue);
        Assert.Equal(60m, margin.Cost);
        Assert.Equal(40m, margin.Margin);
        Assert.Equal(0.4, margin.MarginRate);

        var item = Assert.Single(report.Items);
        Assert.Equal(60m, item.UnitCost);
        Assert.True(item.HasCost);
    }

    /// <summary>
    /// A sale that never reached SAP carries no cost, so it must not reach the margin either.
    /// Costing it would put its whole revenue against a cost of nothing and report it as pure
    /// profit — the flattering direction, as every failure on this report is.
    /// </summary>
    [Fact]
    public async Task An_unposted_sale_is_left_out_of_the_margin_entirely()
    {
        AddSale(Rep, "POSTED", 100m, new DateTime(2026, 8, 4), posted: true);
        AddSale(Rep, "HELD", 500m, new DateTime(2026, 8, 5), posted: false);
        await _context.SaveChangesAsync();

        var report = await RunAsync(unitCosts: new() { ["CHE011"] = 60m });

        var margin = Assert.Single(report.Summary.MarginByCurrency);

        // The posted sale only — not 600.
        Assert.Equal(100m, margin.Revenue);
        Assert.Equal(60m, margin.Cost);

        // While the revenue figure still reports everything that sold.
        Assert.Equal(600m, Assert.Single(report.Summary.RevenueByCurrency).Gross);
    }

    /// <summary>
    /// SAP states cost in the company currency and the vans bill in two. A ZiG sale gets revenue and
    /// no margin — and no margin row at all, because a row holding a null invites somebody to read
    /// it as zero.
    /// </summary>
    [Fact]
    public async Task A_sale_in_another_currency_gets_revenue_but_no_margin()
    {
        AddSale(Rep, "USD1", 100m, new DateTime(2026, 8, 4), posted: true);
        AddSale(Rep, "ZIG1", 4000m, new DateTime(2026, 8, 5), posted: true, currency: "ZWG");
        await _context.SaveChangesAsync();

        var report = await RunAsync(unitCosts: new() { ["CHE011"] = 60m });

        // Two revenue rows, one margin row.
        Assert.Equal(2, report.Summary.RevenueByCurrency.Count);
        var margin = Assert.Single(report.Summary.MarginByCurrency);
        Assert.Equal("USD", margin.Currency);

        Assert.Contains("ZWG", report.Quality.CurrenciesWithoutMatchingCost);
        Assert.Contains(
            report.Quality.Caveats,
            caveat => caveat.Contains("No margin is stated for ZWG"));
    }

    /// <summary>
    /// With the company currency unreadable — SAP down, or the statement refused — nothing can be
    /// costed, because a cost of unknown denomination cannot be subtracted from anything. The rest
    /// of the report is local and still true, so it renders.
    /// </summary>
    [Fact]
    public async Task An_unreadable_company_currency_costs_nothing_and_says_so()
    {
        AddSale(Rep, "S1", 100m, new DateTime(2026, 8, 4), posted: true);
        await _context.SaveChangesAsync();

        var report = await RunAsync(localCurrency: null, unitCosts: new() { ["CHE011"] = 60m });

        Assert.False(report.Summary.MarginAvailable);
        Assert.Null(report.Summary.CostCurrency);
        Assert.Empty(report.Summary.MarginByCurrency);
        Assert.False(report.Quality.CostAvailable);

        // And the local half is intact.
        Assert.Equal(100m, Assert.Single(report.Summary.RevenueByCurrency).Gross);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("No cost could be read from SAP"));
    }

    /// <summary>
    /// B1 leaves the cost column at zero on a line whose item has no valuation yet. Carrying that
    /// through would report the item as all margin, which is the single most flattering thing this
    /// report could do.
    /// </summary>
    [Fact]
    public async Task An_item_sap_carries_no_valuation_for_is_not_treated_as_pure_profit()
    {
        AddSale(Rep, "S1", 100m, new DateTime(2026, 8, 4), posted: true);
        await _context.SaveChangesAsync();

        var report = await RunAsync(unitCosts: new() { ["CHE011"] = 0m });

        var item = Assert.Single(report.Items);
        Assert.Null(item.UnitCost);
        Assert.False(item.HasCost);
        Assert.Empty(report.Summary.MarginByCurrency);

        Assert.Equal(1, report.Quality.ItemsWithoutCost);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("no usable cost"));
    }

    /// <summary>
    /// Asking for the report without costs does one local read and states no margin. The caveat
    /// distinguishes it from a run where SAP was asked and could not answer — those are different
    /// facts and a reader acts differently on them.
    /// </summary>
    [Fact]
    public async Task Skipping_the_cost_fetch_is_reported_differently_from_a_failed_one()
    {
        AddSale(Rep, "S1", 100m, new DateTime(2026, 8, 4), posted: true);
        await _context.SaveChangesAsync();

        var report = await RunAsync(unitCosts: new() { ["CHE011"] = 60m }, includeCost: false);

        Assert.False(report.Summary.MarginAvailable);
        Assert.False(report.Quality.CostAttempted);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("Costs were not fetched"));
        Assert.DoesNotContain(report.Quality.Caveats, caveat => caveat.Contains("No cost could be read"));
    }

    /// <summary>
    /// A margin can be negative, and that is a finding rather than an error. Clamping it at zero
    /// would hide the one thing a margin report exists to surface.
    /// </summary>
    [Fact]
    public async Task An_item_sold_below_cost_reports_a_negative_margin()
    {
        AddSale(Rep, "S1", 40m, new DateTime(2026, 8, 4), posted: true);
        await _context.SaveChangesAsync();

        var report = await RunAsync(unitCosts: new() { ["CHE011"] = 60m });

        var margin = Assert.Single(report.Summary.MarginByCurrency);
        Assert.Equal(-20m, margin.Margin);
        Assert.Equal(-0.5, margin.MarginRate);
    }

    // --- The costable share ---

    /// <summary>
    /// A sale that never reached SAP can never be costed, however real its revenue. This is the
    /// figure the whole report exists to publish.
    /// </summary>
    [Fact]
    public async Task A_sale_that_never_reached_sap_is_not_costable()
    {
        AddSale(Rep, "POSTED", 100m, new DateTime(2026, 8, 4), posted: true);
        AddSale(Rep, "HELD", 60m, new DateTime(2026, 8, 5), posted: false);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Summary.LineCount);
        Assert.Equal(1, report.Summary.PostedLineCount);
        Assert.Equal(0.5, report.Summary.CostableLineShare);

        // The revenue splits the same way, and never folds across the two.
        Assert.Equal(160m, Assert.Single(report.Summary.RevenueByCurrency).Gross);
        Assert.Equal(100m, Assert.Single(report.Summary.CostableRevenueByCurrency).Gross);

        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("has not reached SAP"));
    }

    /// <summary>
    /// A van sale line always carries a warehouse, and the report depends on that: the warehouse is
    /// the key the cost will join on.
    ///
    /// This started as a test that a warehouse-less line is reported as uncostable, and it could not
    /// be written — the database refuses the row. Both line entities mark the column required, and
    /// the offline ingest refuses an entire batch from a rep with no assigned warehouse rather than
    /// accepting one. So the report carries no "lines with no warehouse" figure: it could never be
    /// anything but zero, and a counter that cannot move reads as a check that passed rather than
    /// one that cannot fail. This pins the guarantee the absence rests on.
    /// </summary>
    [Fact]
    public async Task A_line_cannot_be_written_without_a_warehouse()
    {
        AddSale(Rep, "NOWHERE", 100m, new DateTime(2026, 8, 4), posted: true, warehouse: null);

        await Assert.ThrowsAnyAsync<Exception>(() => _context.SaveChangesAsync());
    }

    /// <summary>
    /// The online half posts by its own route and on its own column, so a confirmed reservation
    /// carrying a SAP document number is costable exactly as an offline sale is. Reading only the
    /// offline column would report the online half as permanently unpriceable.
    /// </summary>
    [Fact]
    public async Task An_online_sale_that_posted_is_costable_too()
    {
        AddReservation(Rep, "ONLINE", 80m, new DateTime(2026, 8, 6, 9, 0, 0), sapDocNum: 4242);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Summary.LineCount);
        Assert.Equal(1, report.Summary.PostedLineCount);
        Assert.Equal(1.0, report.Summary.CostableLineShare);
    }

    [Fact]
    public async Task An_online_sale_that_never_posted_is_not_costable()
    {
        AddReservation(Rep, "ONLINE", 80m, new DateTime(2026, 8, 6, 9, 0, 0), sapDocNum: null);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Summary.LineCount);
        Assert.Equal(0, report.Summary.PostedLineCount);
    }

    /// <summary>
    /// Nothing sold means no costable share — not a share of zero, which would read as an estate
    /// whose posting has completely failed rather than one that had a quiet week.
    /// </summary>
    [Fact]
    public async Task An_empty_period_has_no_costable_share_rather_than_a_share_of_zero()
    {
        var report = await RunAsync();

        Assert.Equal(0, report.Summary.LineCount);
        Assert.Null(report.Summary.CostableLineShare);
        Assert.Empty(report.Items);
        Assert.Empty(report.Vans);
        Assert.NotEmpty(report.Quality.Caveats);
    }

    // --- The posting switch ---

    /// <summary>
    /// With the posting job off, the offline half can never be costed however long it waits. That is
    /// a different statement from "these particular sales have not posted yet", and the page has to
    /// make it.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task The_posting_switch_is_reported_alongside_the_costable_share(
        bool enabled,
        bool expectCaveat)
    {
        AddSale(Rep, "HELD", 60m, new DateTime(2026, 8, 5), posted: false);
        await _context.SaveChangesAsync();

        var report = await RunAsync(postingEnabled: enabled);

        Assert.Equal(enabled, report.Quality.PostingJobEnabled);
        Assert.Equal(
            expectCaveat,
            report.Quality.Caveats.Any(caveat => caveat.Contains("posting job is switched off")));
    }

    // --- Grouping ---

    /// <summary>
    /// Money is per currency and never folded. A USD line and a ZiG line are two rows on every
    /// revenue figure the report publishes.
    /// </summary>
    [Fact]
    public async Task Two_currencies_never_become_one_revenue_figure()
    {
        AddSale(Rep, "USD1", 100m, new DateTime(2026, 8, 4));
        AddSale(Rep, "ZIG1", 4000m, new DateTime(2026, 8, 5), currency: "ZWG");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Summary.RevenueByCurrency.Count);
        Assert.Equal(100m, report.Summary.RevenueByCurrency.Single(m => m.Currency == "USD").Gross);
        Assert.Equal(4000m, report.Summary.RevenueByCurrency.Single(m => m.Currency == "ZWG").Gross);
    }

    /// <summary>
    /// The van rows account for every penny in the summary. Since every line carries a warehouse
    /// there is no bucket for the ones that do not, so any shortfall here would be a grouping bug
    /// rather than a disclosed gap — which is why the totals are asserted against each other.
    /// </summary>
    [Fact]
    public async Task The_van_rows_account_for_all_the_revenue()
    {
        AddSale(Rep, "A", 100m, new DateTime(2026, 8, 4), warehouse: "VAN010");
        AddSale(Rep, "B", 40m, new DateTime(2026, 8, 5), warehouse: "VAN011");
        AddSale(Rep, "C", 25m, new DateTime(2026, 8, 6), warehouse: "VAN011");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Vans.Count);
        Assert.Equal(2, report.Summary.VanCount);

        Assert.Equal(
            report.Summary.RevenueByCurrency.Sum(money => money.Gross),
            report.Vans.Sum(van => van.RevenueByCurrency.Sum(money => money.Gross)));

        // Biggest van first — this is a ranking, and the busiest row is the one a reader wants.
        Assert.Equal("VAN010", report.Vans[0].WarehouseCode);
    }

    /// <summary>An item sold from two vans is one item row that knows it spans two.</summary>
    [Fact]
    public async Task An_item_sold_from_two_vans_reports_both()
    {
        AddSale(Rep, "A", 100m, new DateTime(2026, 8, 4), warehouse: "VAN010");
        AddSale(Rep, "B", 60m, new DateTime(2026, 8, 5), warehouse: "VAN011");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var item = Assert.Single(report.Items);
        Assert.Equal(2, item.VanCount);
        Assert.Equal(2, item.LineCount);
        Assert.Equal(160m, Assert.Single(item.RevenueByCurrency).Gross);
    }

    // --- Validation ---

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_rejected()
    {
        var result = await RunRawAsync(From, From.AddDays(-1));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidRange", result.FirstError.Code);
    }

    // --- Harness ---

    private async Task<VanMarginReportResult> RunAsync(
        bool postingEnabled = false,
        string? localCurrency = "USD",
        Dictionary<string, decimal>? unitCosts = null,
        bool includeCost = true)
    {
        var result = await RunRawAsync(From, To, postingEnabled, localCurrency, unitCosts, includeCost);

        Assert.False(result.IsError);
        return result.Value;
    }

    private Task<ErrorOr.ErrorOr<VanMarginReportResult>> RunRawAsync(
        DateTime from,
        DateTime to,
        bool postingEnabled = false,
        string? localCurrency = "USD",
        Dictionary<string, decimal>? unitCosts = null,
        bool includeCost = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VanSalesPosting:Enabled"] = postingEnabled ? "true" : "false"
            })
            .Build();

        return new GetVanMarginReportHandler(
            _context,
            SapClient(localCurrency, unitCosts ?? new Dictionary<string, decimal>()),
            configuration,
            NullLogger<GetVanMarginReportHandler>.Instance).Handle(
            new GetVanMarginReportQuery(from, to, IncludeCost: includeCost),
            CancellationToken.None);
    }

    /// <summary>
    /// Answers the two statements the cost reader makes, in the shape SAP returns: a list of
    /// dictionaries keyed by the column aliases.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="localCurrency"/> stands for SAP refusing the statement or being
    /// unreachable — the case the reader has to survive without a margin rather than by throwing.
    /// Quantity is fixed at 1 per row so the weighted unit cost is exactly the price given, which
    /// keeps the arithmetic under test in the handler rather than in the fixture.
    /// </remarks>
    private static ISAPServiceLayerClient SapClient(
        string? localCurrency,
        Dictionary<string, decimal> unitCosts) =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.ExecuteRawSqlQueryAsync) =>
                localCurrency is null
                    ? throw new InvalidOperationException("SAP refused the statement")
                    : Task.FromResult(new List<Dictionary<string, object?>>
                    {
                        new() { ["MainCurncy"] = localCurrency }
                    }),

            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync) =>
                Task.FromResult(unitCosts
                    .Select(pair => new Dictionary<string, object?>
                    {
                        ["ItemCode"] = pair.Key,
                        ["WhsCode"] = ((IReadOnlyDictionary<string, string>)args![3]!)["warehouseCode"],
                        ["DocCur"] = "USD",
                        ["Quantity"] = 1m,
                        ["StockPrice"] = pair.Value
                    })
                    .ToList()),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    private void AddSale(
        Guid userId,
        string reference,
        decimal total,
        DateTime docDate,
        bool posted = false,
        string? warehouse = VanWarehouse,
        string currency = "USD",
        string itemCode = "CHE011") =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = VanAccount,
            CardName = VanAccount,
            RouteCustomerCode = "SHOP1",
            RouteCustomerName = "Shop One",
            DocDate = docDate,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            // Deliberately nullable here. The column is required and one test passes null to prove
            // the database refuses the row — which is what the report's missing "no warehouse"
            // counter rests on.
            WarehouseCode = warehouse!,
            PaymentMethod = "Cash",
            AmountPaid = total,
            CreatedBy = userId.ToString(),
            SapDocNum = posted ? 9001 : null,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = itemCode,
                    ItemDescription = $"Item {itemCode}",
                    Quantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = warehouse!
                }
            ]
        });

    private void AddReservation(
        Guid userId,
        string reference,
        decimal total,
        DateTime createdAtUtc,
        int? sapDocNum,
        string? warehouse = VanWarehouse,
        string itemCode = "CHE011") =>
        _context.StockReservations.Add(new StockReservationEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = VanAccount,
            RouteCustomerCode = "SHOP1",
            RouteCustomerName = "Shop One",
            Status = ReservationStatus.Confirmed,
            CreatedAt = createdAtUtc,
            ExpiresAt = createdAtUtc.AddMinutes(5),
            TotalValue = total,
            Currency = "USD",
            PaymentMethod = "Cash",
            CreatedBy = userId.ToString(),
            SAPDocNum = sapDocNum,
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = itemCode,
                    ItemDescription = $"Item {itemCode}",
                    OriginalQuantity = 1m,
                    ReservedQuantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = warehouse!
                }
            ]
        });
}
