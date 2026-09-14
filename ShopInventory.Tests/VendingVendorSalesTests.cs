using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSalesSummary;
using ShopInventory.Models.Entities;
using ShopInventory.Web.Features.Vending;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The sales figures on /vending: each vendor's month to date and last sale on the list, and the vendor
/// page's daily chart, product mix, recent invoices and posting counts.
///
/// None of it has an endpoint of its own. The page reads the route-customer reports, which were written
/// for vans, so the first thing worth proving is that a vending till's sale — invoiced on the depot's
/// partner and tied to the vendor by id — reaches them at all. The rest is the page's arithmetic over what
/// comes back, run on the API's real output passed through the Web's hand-mirrored models, so a field the
/// two sides disagree on fails here rather than reading as "no sales" on the page.
/// </summary>
public sealed class VendingVendorSalesTests : IDisposable
{
    private const string Depot = "COR006";
    private const string OtherDepot = "COR008";

    private static readonly DateTime Today = new(2026, 9, 14);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private int _reference;

    public VendingVendorSalesTests()
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

    // ── The API reads a vending sale ───────────────────────────────────────

    [Fact]
    public async Task A_vending_sale_counts_for_its_own_vendor_in_the_depot_summary()
    {
        var tendai = Vendor("VMP001", Depot);
        var prisca = Vendor("VMP002", Depot);
        var nkosana = Vendor("VMB001", OtherDepot);
        await _context.SaveChangesAsync();

        Vend(tendai, 120.50m, Today.AddDays(-1));
        Vend(tendai, 79.50m, Today);
        Vend(tendai, 30m, new DateTime(2026, 8, 30));
        Vend(nkosana, 999m, Today, cardCode: OtherDepot);
        await _context.SaveChangesAsync();

        var summary = await SummaryAsync(Depot, VendorSalesDigest.MonthStart(Today), Today);

        var route = Assert.Single(summary.Routes);
        Assert.Equal(Depot, route.AssignedBusinessPartnerCode);

        var tendaiRow = Assert.Single(route.Customers, row => row.RouteCustomerId == tendai.Id);
        Assert.Equal(2, tendaiRow.SaleCount);
        Assert.Equal("USD 200.00", VendorSalesDigest.MoneyLabel(tendaiRow.TotalsByCurrency));
        Assert.Equal(Today, tendaiRow.LastSaleAt);

        // In the depot's book, never bought, and not borrowing another depot's takings.
        var priscaRow = Assert.Single(route.Customers, row => row.RouteCustomerId == prisca.Id);
        Assert.Equal(0, priscaRow.SaleCount);
        Assert.Null(priscaRow.LastSaleAt);
        Assert.Equal("—", VendorSalesDigest.MoneyLabel(priscaRow.TotalsByCurrency));
        Assert.DoesNotContain(route.Customers, row => row.RouteCustomerId == nkosana.Id);
    }

    [Fact]
    public async Task The_last_sale_on_the_list_is_all_time_not_this_month()
    {
        var memory = Vendor("VMB002", Depot);
        await _context.SaveChangesAsync();
        Vend(memory, 55m, new DateTime(2026, 8, 20));
        await _context.SaveChangesAsync();

        var row = (await SummaryAsync(Depot, VendorSalesDigest.MonthStart(Today), Today))
            .Routes.Single().Customers.Single();

        Assert.Equal(0, row.SaleCount);
        Assert.Equal(new DateTime(2026, 8, 20), row.LastSaleAt);
        Assert.Equal("20 Aug 2026", VendorSalesDigest.LastSaleLabel(row.LastSaleAt, Today));
    }

    [Fact]
    public async Task A_removed_vendor_still_reports_so_the_removed_filter_has_figures()
    {
        var gone = Vendor("VMP009", Depot, isActive: false);
        await _context.SaveChangesAsync();
        Vend(gone, 12m, Today.AddDays(-2));
        await _context.SaveChangesAsync();

        var row = (await SummaryAsync(Depot, VendorSalesDigest.MonthStart(Today), Today))
            .Routes.Single().Customers.Single();

        Assert.False(row.IsActive);
        Assert.Equal(1, row.SaleCount);
    }

    // ── The vendor page's arithmetic, over the API's real output ───────────

    [Fact]
    public async Task The_vendor_page_reads_month_to_date_daily_and_mix_from_the_sales_endpoint()
    {
        var tendai = Vendor("VMP001", Depot);
        await _context.SaveChangesAsync();

        Vend(tendai, 113m, Today, lines: [("MAHEU500", 40, 60m), ("MILK2L", 10, 40m)]);
        Vend(tendai, 56.50m, Today, lines: [("MILK2L", 12, 50m)]);
        Vend(tendai, 22.60m, new DateTime(2026, 9, 2), lines: [("CHED250", 2, 20m)]);
        // The day before the window: never read, so it cannot leak into the month or the mix.
        Vend(tendai, 45.20m, new DateTime(2026, 8, 31), lines: [("MAHEU500", 30, 40m)]);
        // Another currency, the same day: counted aside, never added in.
        Vend(tendai, 900m, Today, currency: "ZWG", lines: [("MILK2L", 5, 800m)]);
        await _context.SaveChangesAsync();

        var sales = await DetailThroughTheWebAsync(tendai.Id, VendorSalesDigest.WindowStart(Today), Today);

        var month = VendorSalesDigest.SummariseMonth(sales.Sales, Today);
        Assert.Equal("USD", month.Currency);
        Assert.Equal(192.10m, month.Gross);
        Assert.Equal(3, month.SaleCount);
        Assert.Equal(4, month.LineCount);
        Assert.Equal(64m, month.Units);
        Assert.Equal("ZWG", Assert.Single(month.OtherCurrencies).Currency);

        var days = VendorSalesDigest.Daily(sales.Sales, Today, month.Currency);
        Assert.Equal(VendorSalesDigest.DailyWindowDays, days.Count);
        Assert.Equal(new DateTime(2026, 9, 1), days[0].Date);
        Assert.Equal(Today, days[^1].Date);
        Assert.Equal(169.50m, days[^1].Gross);
        Assert.Equal(2, days[^1].SaleCount);
        Assert.Equal(0m, days.Single(day => day.Date == new DateTime(2026, 9, 3)).Gross);

        var mix = VendorSalesDigest.ProductMix(sales.Sales, Today, month.Currency, top: 5);
        Assert.Equal(["MILK2L", "MAHEU500", "CHED250"], mix.Select(item => item.ItemCode));
        Assert.Equal(90m, mix[0].Value);
        Assert.Equal(22m, mix[0].Units);
        Assert.Equal(60m, mix[1].Value);
    }

    [Fact]
    public async Task The_window_reaches_back_to_whichever_starts_first_the_month_or_the_chart()
    {
        Assert.Equal(new DateTime(2026, 9, 1), VendorSalesDigest.WindowStart(new DateTime(2026, 9, 14)));
        Assert.Equal(new DateTime(2026, 9, 1), VendorSalesDigest.WindowStart(new DateTime(2026, 9, 30)));
        Assert.Equal(new DateTime(2026, 8, 22), VendorSalesDigest.WindowStart(new DateTime(2026, 9, 4)));

        var tendai = Vendor("VMP001", Depot);
        await _context.SaveChangesAsync();
        Vend(tendai, 10m, new DateTime(2026, 8, 22));
        Vend(tendai, 10m, new DateTime(2026, 8, 21));
        await _context.SaveChangesAsync();

        var early = new DateTime(2026, 9, 4);
        var sales = await DetailThroughTheWebAsync(tendai.Id, VendorSalesDigest.WindowStart(early), early);

        Assert.Equal(new DateTime(2026, 8, 22), Assert.Single(sales.Sales).SoldAt.Date);
        Assert.Equal(10m, VendorSalesDigest.Daily(sales.Sales, early, "USD")[0].Gross);
    }

    [Fact]
    public async Task Posting_counts_follow_the_status_the_api_writes()
    {
        var tendai = Vendor("VMP001", Depot);
        await _context.SaveChangesAsync();

        Vend(tendai, 10m, Today, consolidation: DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 5001);
        Vend(tendai, 10m, Today, consolidation: DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 5002);
        Vend(tendai, 10m, Today);
        Vend(tendai, 10m, Today, consolidation: DesktopSaleConsolidationStatus.Failed);
        Vend(tendai, 10m, Today, consolidation: DesktopSaleConsolidationStatus.Excluded);
        await _context.SaveChangesAsync();

        var sales = await DetailThroughTheWebAsync(tendai.Id, VendorSalesDigest.MonthStart(Today), Today);
        var posting = VendorSalesDigest.PostingSince(sales.Sales, VendorSalesDigest.MonthStart(Today));

        Assert.Equal(new VendorSalesDigest.Posting(Posted: 2, Awaiting: 1, Failed: 1, Excluded: 1), posting);
    }

    /// <summary>
    /// The page reads the API's status sentence, so every sentence the API can write has to land in a
    /// bucket someone would agree with. Enumerated rather than sampled: a new status added on the API side
    /// fails here instead of quietly counting as "awaiting".
    /// </summary>
    [Fact]
    public void Every_status_the_api_can_describe_lands_in_the_right_posting_bucket()
    {
        foreach (var consolidation in Enum.GetValues<DesktopSaleConsolidationStatus>())
        {
            foreach (var receipt in Enum.GetValues<DesktopSaleReceiptIngestStatus>())
            {
                foreach (int? docNum in new int?[] { null, 7001 })
                {
                    var status = RouteCustomerSalesReporting.DescribeOfflineSaleStatus(consolidation, receipt, docNum);
                    var receiptNeedsSomeone = receipt is DesktopSaleReceiptIngestStatus.Failed
                        or DesktopSaleReceiptIngestStatus.ChainBroken
                        or DesktopSaleReceiptIngestStatus.Unsignable;

                    var expected = consolidation == DesktopSaleConsolidationStatus.Failed || receiptNeedsSomeone
                        ? VendorSalesDigest.PostingState.Failed
                        : consolidation switch
                        {
                            DesktopSaleConsolidationStatus.Consolidated => VendorSalesDigest.PostingState.Posted,
                            DesktopSaleConsolidationStatus.Excluded => VendorSalesDigest.PostingState.Excluded,
                            _ => VendorSalesDigest.PostingState.Awaiting
                        };

                    Assert.True(expected == VendorSalesDigest.PostingOf(status),
                        $"\"{status}\" ({consolidation}, {receipt}, {docNum}) read as {VendorSalesDigest.PostingOf(status)}, expected {expected}");
                }
            }
        }
    }

    // ── Labels ──────────────────────────────────────────────────────────────

    [Fact]
    public void Money_names_its_currency_and_owns_up_to_the_others()
    {
        Assert.Equal("—", VendorSalesDigest.MoneyLabel(null));
        Assert.Equal("—", VendorSalesDigest.MoneyLabel([]));
        Assert.Equal("USD 1,204.50", VendorSalesDigest.MoneyLabel([Totals("USD", 3, 1204.50m)]));
        Assert.Equal("ZWG 9,000.00 + USD", VendorSalesDigest.MoneyLabel([Totals("USD", 1, 40m), Totals("ZWG", 4, 9000m)]));
        Assert.Equal("USD 1,205", VendorSalesDigest.MoneyLabel([Totals("USD", 3, 1204.50m)], decimals: 0));
    }

    [Fact]
    public void Initials_and_last_sale_read_the_way_the_design_does()
    {
        Assert.Equal("TM", VendorSalesDigest.Initials("Tendai", "Mushore"));
        Assert.Equal("TM", VendorSalesDigest.Initials("Tendai Mushore"));
        Assert.Equal("T", VendorSalesDigest.Initials("Tendai", "  "));
        Assert.Equal("?", VendorSalesDigest.Initials("", null));

        Assert.Equal("Never", VendorSalesDigest.LastSaleLabel(null, Today));
        Assert.Equal("Today", VendorSalesDigest.LastSaleLabel(Today, Today));
        Assert.Equal("Yesterday", VendorSalesDigest.LastSaleLabel(Today.AddDays(-1), Today));
        Assert.Equal("4 days ago", VendorSalesDigest.LastSaleLabel(Today.AddDays(-4), Today));
        Assert.Equal("01 Sep 2026", VendorSalesDigest.LastSaleLabel(new DateTime(2026, 9, 1), Today));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static RouteCustomerSalesTotalsModel Totals(string currency, int count, decimal gross) =>
        new() { Currency = currency, SaleCount = count, Gross = gross };

    private RouteCustomerEntity Vendor(string code, string depot, bool isActive = true)
    {
        var vendor = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = depot,
            Code = code,
            Name = $"Vendor {code}",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
        };
        _context.RouteCustomers.Add(vendor);
        return vendor;
    }

    private void Vend(
        RouteCustomerEntity vendor,
        decimal total,
        DateTime day,
        string currency = "USD",
        string cardCode = Depot,
        DesktopSaleConsolidationStatus consolidation = DesktopSaleConsolidationStatus.Pending,
        int? sapDocNum = null,
        (string Code, int Quantity, decimal Net)[]? lines = null)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"VEND-{++_reference}",
            SourceSystem = SaleSourceSystems.Vending,
            CardCode = cardCode,
            WarehouseCode = "KEFGRC",
            DocDate = day,
            TotalAmount = total,
            VatAmount = Math.Round(total * 0.13m, 2),
            AmountPaid = total,
            Currency = currency,
            PaymentMethod = TenderTypes.Cash,
            ConsolidationStatus = consolidation,
            SapDocNum = sapDocNum,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            RouteCustomerId = vendor.Id,
            RouteCustomerCode = vendor.Code,
            RouteCustomerName = vendor.Name,
            CreatedBy = "cashier",
            CreatedAt = DateTime.SpecifyKind(day.AddHours(8), DateTimeKind.Utc),
            Lines = (lines ?? [("MAHEU500", 1, total)])
                .Select((line, index) => new DesktopSaleLineEntity
                {
                    LineNum = index + 1,
                    ItemCode = line.Code,
                    ItemDescription = line.Code,
                    Quantity = line.Quantity,
                    UnitPrice = Math.Round(line.Net / line.Quantity, 2),
                    LineTotal = line.Net,
                    WarehouseCode = "KEFGRC",
                })
                .ToList(),
        });
    }

    private async Task<RouteCustomerSalesSummaryModel> SummaryAsync(string depot, DateTime from, DateTime to)
    {
        var result = await new GetRouteCustomerSalesSummaryHandler(_context).Handle(
            new GetRouteCustomerSalesSummaryQuery(depot, from, to, null, true),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return ThroughTheWire<RouteCustomerSalesSummaryDto, RouteCustomerSalesSummaryModel>(result.Value);
    }

    private async Task<RouteCustomerSalesDetailModel> DetailThroughTheWebAsync(int vendorId, DateTime from, DateTime to)
    {
        _context.ChangeTracker.Clear();
        var result = await new GetRouteCustomerSalesHandler(_context).Handle(
            new GetRouteCustomerSalesQuery(vendorId, from, to),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return ThroughTheWire<RouteCustomerSalesDetailDto, RouteCustomerSalesDetailModel>(result.Value);
    }

    /// <summary>What the Web's HttpClient does to the API's answer: web JSON out, web JSON in.</summary>
    private static TModel ThroughTheWire<TDto, TModel>(TDto dto)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.Deserialize<TModel>(JsonSerializer.Serialize(dto, options), options)!;
    }
}
