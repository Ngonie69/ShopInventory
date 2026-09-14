using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Web.Features.Vending;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using WebAnalysis = ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis.DesktopSalesAnalysisResult;

namespace ShopInventory.Tests;

/// <summary>
/// /vending/sales: every invoice a depot cashier raised, and the month's figures above them.
///
/// The page adds nothing to the API but two things on the sales list — the vendor each sale was made to,
/// and a search — so those are proved on the real handler. The figures are the page's arithmetic over the
/// real sales analysis, passed through the Web's hand-mirrored models the way the HttpClient passes them,
/// so a field the two sides disagree on fails here rather than reading as "no sales" on the page.
/// </summary>
public sealed class VendingSalesListTests : IDisposable
{
    private const string Graniteside = "COR006";
    private const string Bulawayo = "COR008";

    private static readonly DateTime Today = new(2026, 9, 14);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private int _reference;

    public VendingSalesListTests()
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

    // ── The list ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_listed_sale_names_the_vendor_it_was_made_to_through_to_the_web_model()
    {
        var tendai = Vendor("VMP001", "Tendai Mushore", Graniteside);
        await _context.SaveChangesAsync();
        Sell(tendai, 42.50m, Today);
        await _context.SaveChangesAsync();

        var sale = Assert.Single((await ListThroughTheWebAsync(new GetDesktopSalesQuery(await ConsoleAsync(), SourceSystem: SaleSourceSystems.Vending))).Sales);

        Assert.Equal(tendai.Id, sale.RouteCustomerId);
        Assert.Equal("VMP001", sale.RouteCustomerCode);
        Assert.Equal("Tendai Mushore", sale.RouteCustomerName);
        Assert.Equal(Graniteside, sale.CardCode);
    }

    [Fact]
    public async Task A_sale_to_a_business_partner_names_no_vendor()
    {
        Sell(vendor: null, 10m, Today, source: SaleSourceSystems.ShopTill);
        await _context.SaveChangesAsync();

        var sale = Assert.Single((await ListThroughTheWebAsync(new GetDesktopSalesQuery(await ConsoleAsync()))).Sales);

        Assert.Null(sale.RouteCustomerId);
        Assert.Null(sale.RouteCustomerCode);
        Assert.Null(sale.RouteCustomerName);
    }

    [Theory]
    [InlineData("vend-2", "VEND-2")]      // the till reference, in any case
    [InlineData("fr-77", "VEND-3")]       // the fiscal receipt number
    [InlineData("vmb", "VEND-4")]         // part of the vendor's code
    [InlineData("chibanda", "VEND-2")]    // part of the vendor's name
    [InlineData("88123", "VEND-5")]       // the SAP invoice it posted as
    public async Task Search_finds_a_sale_by_what_someone_holding_it_would_type(string search, string expected)
    {
        var prisca = Vendor("VMP002", "Prisca Chibanda", Graniteside);
        var nkosana = Vendor("VMB004", "Nkosana Dube", Bulawayo);
        var farai = Vendor("VMM007", "Farai Zuze", Graniteside);
        await _context.SaveChangesAsync();

        Sell(farai, 10m, Today);                                             // VEND-1
        Sell(prisca, 10m, Today);                                            // VEND-2
        Sell(farai, 10m, Today, fiscalReceipt: "FR-771");                    // VEND-3
        Sell(nkosana, 10m, Today, cardCode: Bulawayo);                       // VEND-4
        Sell(farai, 10m, Today, sapDocNum: 88123,                            // VEND-5
            consolidation: DesktopSaleConsolidationStatus.Consolidated);
        await _context.SaveChangesAsync();

        var found = await ListThroughTheWebAsync(
            new GetDesktopSalesQuery(await ConsoleAsync(), SourceSystem: SaleSourceSystems.Vending, Search: search));

        Assert.Equal([expected], found.Sales.Select(sale => sale.ExternalReferenceId));
        Assert.Equal(1, found.TotalCount);
    }

    [Fact]
    public async Task Search_narrows_the_other_filters_and_never_widens_them()
    {
        var prisca = Vendor("VMP002", "Prisca Chibanda", Graniteside);
        var twin = Vendor("VMB002", "Prisca Chibanda", Bulawayo);
        await _context.SaveChangesAsync();

        Sell(prisca, 10m, Today);                                                   // VEND-1
        Sell(twin, 10m, Today, cardCode: Bulawayo);                                 // another depot
        Sell(prisca, 10m, new DateTime(2026, 8, 31));                               // last month
        Sell(prisca, 10m, Today, source: SaleSourceSystems.ShopTill);               // not vending
        await _context.SaveChangesAsync();

        var found = await ListThroughTheWebAsync(new GetDesktopSalesQuery(
            await ConsoleAsync(),
            CardCode: Graniteside,
            FromDate: VendorSalesDigest.MonthStart(Today),
            ToDate: Today,
            SourceSystem: SaleSourceSystems.Vending,
            Search: "prisca"));

        Assert.Equal(["VEND-1"], found.Sales.Select(sale => sale.ExternalReferenceId));
    }

    /// <summary>
    /// The list gives a consolidation status where a vendor's page reads the route-customer report's
    /// sentence. Enumerated, so both pages put a sale in the same bucket and a new status fails here.
    /// </summary>
    [Fact]
    public void Every_consolidation_status_reads_as_the_vendor_page_reads_it()
    {
        foreach (var status in Enum.GetValues<DesktopSaleConsolidationStatus>())
        {
            foreach (int? docNum in new int?[] { null, 7001 })
            {
                var sentence = RouteCustomerSalesReporting.DescribeOfflineSaleStatus(
                    status, DesktopSaleReceiptIngestStatus.NotApplicable, docNum);

                Assert.Equal(VendorSalesDigest.PostingOf(sentence), VendingSalesDigest.PostingOf(status.ToString()));
            }
        }
    }

    // ── The figures ────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_depot_adds_up_its_own_warehouses_and_an_unclaimed_one_is_kept()
    {
        var tendai = Vendor("VMP001", "Tendai Mushore", Graniteside);
        var nkosana = Vendor("VMB001", "Nkosana Dube", Bulawayo);
        await _context.SaveChangesAsync();

        Sell(tendai, 100m, Today, warehouse: "KEFGRC");
        Sell(tendai, 50m, Today.AddDays(-3), warehouse: "KEFGRX");
        Sell(nkosana, 80m, Today, cardCode: Bulawayo, warehouse: "KEFBYC");
        Sell(nkosana, 5m, Today, cardCode: Bulawayo, warehouse: "STRAY");
        // A shop till in a depot's warehouse, and a vending sale in another currency: neither may count.
        Sell(vendor: null, 999m, Today, warehouse: "KEFGRC", source: SaleSourceSystems.ShopTill);
        Sell(tendai, 700m, Today, warehouse: "KEFGRC", currency: "ZWG");
        await _context.SaveChangesAsync();

        var month = await AnalyseThroughTheWebAsync(VendorSalesDigest.MonthStart(Today), Today);
        var currency = VendingSalesDigest.PrimaryCurrency(month);
        var section = VendingSalesDigest.Section(month, currency);

        Assert.Equal("USD", currency);
        Assert.Equal(["ZWG 700.00"], VendingSalesDigest.OtherCurrencies(month, currency));

        var depots = new List<VendingDepotModel>
        {
            new() { BusinessPartnerCode = Graniteside, WarehouseCodes = ["KEFGRC", "KEFGRX"] },
            new() { BusinessPartnerCode = Bulawayo, WarehouseCodes = ["KEFBYC", "KEFGRC"] },
            new() { BusinessPartnerCode = "MAC006", WarehouseCodes = ["CORMACH"] },
        };
        var rows = VendingSalesDigest.ByDepot(section, depots, code => $"Depot {code}");

        Assert.Equal(
            [
                new VendingSalesDigest.DepotRow(Graniteside, $"Depot {Graniteside}", 2, 150m, IsDepot: true),
                // KEFGRC is Graniteside's first, so Bulawayo does not count it a second time.
                new VendingSalesDigest.DepotRow(Bulawayo, $"Depot {Bulawayo}", 1, 80m, IsDepot: true),
                new VendingSalesDigest.DepotRow("STRAY", "Warehouse STRAY", 1, 5m, IsDepot: false),
                new VendingSalesDigest.DepotRow("MAC006", "Depot MAC006", 0, 0m, IsDepot: true),
            ],
            rows);

        // The rows account for every sale in the section, no more and no fewer.
        Assert.Equal(section!.TotalAmount, rows.Sum(row => row.Gross));
        Assert.Equal(section.SalesCount, rows.Sum(row => row.SaleCount));

        Assert.Equal([new VendingSalesDigest.Tender("Cash", 100m)], VendingSalesDigest.Tenders(section));
    }

    [Fact]
    public async Task The_chart_has_every_day_and_the_change_is_stated_only_against_real_sales()
    {
        var tendai = Vendor("VMP001", "Tendai Mushore", Graniteside);
        await _context.SaveChangesAsync();
        Sell(tendai, 30m, Today);
        Sell(tendai, 20m, Today.AddDays(-13));
        await _context.SaveChangesAsync();

        var from = Today.AddDays(-(VendorSalesDigest.DailyWindowDays - 1));
        var fortnight = await AnalyseThroughTheWebAsync(from, Today);
        var days = VendingSalesDigest.Daily(VendingSalesDigest.Section(fortnight, "USD"), from, Today);

        Assert.Equal(VendorSalesDigest.DailyWindowDays, days.Count);
        Assert.Equal(new VendingSalesDigest.Day(from, 20m, 1), days[0]);
        Assert.Equal(new VendingSalesDigest.Day(Today, 30m, 1), days[^1]);
        Assert.All(days[1..^1], day => Assert.Equal(0, day.SaleCount));

        // Nothing sold in the fortnight before, so there is no percentage to state.
        Assert.Null(VendingSalesDigest.ChangePercent(VendingSalesDigest.Section(fortnight, "USD")));

        Sell(tendai, 40m, from.AddDays(-1));
        await _context.SaveChangesAsync();
        var again = await AnalyseThroughTheWebAsync(from, Today);
        Assert.Equal(25m, VendingSalesDigest.ChangePercent(VendingSalesDigest.Section(again, "USD")));
    }

    [Fact]
    public void Nothing_sold_reads_as_no_currency_and_quiet_days()
    {
        Assert.Null(VendingSalesDigest.PrimaryCurrency(null));
        Assert.Null(VendingSalesDigest.Section(null, null));
        Assert.Empty(VendingSalesDigest.Tenders(null));
        Assert.All(VendingSalesDigest.Daily(null, Today.AddDays(-2), Today), day => Assert.Equal(0m, day.Gross));
    }

    // ── Labels ─────────────────────────────────────────────────────────────

    [Fact]
    public void Ranges_and_times_read_the_way_the_design_does()
    {
        Assert.Equal(Today, VendingSalesDigest.RangeStart(VendingSalesDigest.Range.Today, Today));
        Assert.Equal(new DateTime(2026, 9, 8), VendingSalesDigest.RangeStart(VendingSalesDigest.Range.Week, Today));
        Assert.Equal(new DateTime(2026, 9, 1), VendingSalesDigest.RangeStart(VendingSalesDigest.Range.Month, Today));

        // Stored in UTC, read in CAT: two hours on, which can carry a sale over midnight.
        Assert.Equal("Today 11:42", VendingSalesDigest.When(new DateTime(2026, 9, 14, 9, 42, 0, DateTimeKind.Utc), Today));
        Assert.Equal("Today 00:30", VendingSalesDigest.When(new DateTime(2026, 9, 13, 22, 30, 0, DateTimeKind.Utc), Today));
        Assert.Equal("Yesterday 16:20", VendingSalesDigest.When(new DateTime(2026, 9, 13, 14, 20, 0, DateTimeKind.Utc), Today));
        Assert.Equal("12 Sep 08:40", VendingSalesDigest.When(new DateTime(2026, 9, 12, 6, 40, 0, DateTimeKind.Utc), Today));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private RouteCustomerEntity Vendor(string code, string name, string depot)
    {
        var vendor = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = depot,
            Code = code,
            Name = name,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        _context.RouteCustomers.Add(vendor);
        return vendor;
    }

    private void Sell(
        RouteCustomerEntity? vendor,
        decimal total,
        DateTime day,
        string cardCode = Graniteside,
        string warehouse = "KEFGRC",
        string currency = "USD",
        string source = SaleSourceSystems.Vending,
        string? fiscalReceipt = null,
        int? sapDocNum = null,
        DesktopSaleConsolidationStatus consolidation = DesktopSaleConsolidationStatus.Pending)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"VEND-{++_reference}",
            SourceSystem = source,
            CardCode = cardCode,
            WarehouseCode = warehouse,
            DocDate = day,
            TotalAmount = total,
            VatAmount = Math.Round(total * 0.13m, 2),
            AmountPaid = total,
            Currency = currency,
            PaymentMethod = TenderTypes.Cash,
            ConsolidationStatus = consolidation,
            SapDocNum = sapDocNum,
            FiscalReceiptNumber = fiscalReceipt,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            RouteCustomerId = vendor?.Id,
            RouteCustomerCode = vendor?.Code,
            RouteCustomerName = vendor?.Name,
            CreatedBy = "cashier",
            CreatedAt = DateTime.SpecifyKind(day.AddHours(8), DateTimeKind.Utc),
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 1,
                    ItemCode = "MAHEU500",
                    ItemDescription = "Maheu 500ml",
                    Quantity = 1,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = warehouse,
                },
            ],
        });
    }

    /// <summary>A Cashier with no shop, which reads across every depot as the vending page's users do.</summary>
    private async Task<Guid> ConsoleAsync()
    {
        var id = Guid.NewGuid();
        _context.Users.Add(new User
        {
            Id = id,
            Username = $"console{id:N}"[..12],
            PasswordHash = "x",
            Role = ApplicationRoles.Cashier,
            IsActive = true,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private async Task<DesktopSalesListResponse> ListThroughTheWebAsync(GetDesktopSalesQuery query)
    {
        _context.ChangeTracker.Clear();
        var result = await new GetDesktopSalesHandler(_context, new RecordingAuditService())
            .Handle(query, CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return ThroughTheWire<DesktopSalesListResult, DesktopSalesListResponse>(result.Value);
    }

    private async Task<WebAnalysis> AnalyseThroughTheWebAsync(DateTime from, DateTime to)
    {
        _context.ChangeTracker.Clear();
        var result = await new GetDesktopSalesAnalysisHandler(_context, new RecordingAuditService())
            .Handle(
                new GetDesktopSalesAnalysisQuery(await ConsoleAsync(), from, to, SourceSystem: SaleSourceSystems.Vending),
                CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return ThroughTheWire<DesktopSalesAnalysisResult, WebAnalysis>(result.Value);
    }

    /// <summary>What the Web's HttpClient does to the API's answer: web JSON out, web JSON in.</summary>
    private static TModel ThroughTheWire<TDto, TModel>(TDto dto)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.Deserialize<TModel>(JsonSerializer.Serialize(dto, options), options)!;
    }
}
