using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GenerateEndOfDayReport;
using ShopInventory.Models.Entities;
using ShopInventory.Web.Features.DesktopSalesDay;
using WebDtos = ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The two cards at the head of /desktop-sales, and the end-of-day report they are drawn from.
///
/// What they got wrong: the day card's four lanes counted sales three ways and <i>invoices</i> the
/// fourth — it used <c>PostedInvoiceCount</c>, which counts the customers the 18:00 run raised a
/// consolidation document for. A day whose sales went to SAP one invoice at a time raises no such
/// document, so the card drew four empty lanes under a tally of three sales. Beside it, the "Value on
/// the device" card broke its headline down using <c>BusinessPartnerSummaries</c> — the whole day,
/// consolidated sales and all — so a customer sat at USD 21.66 beneath a headline of USD 0.00.
///
/// Both cases are here: the report's own counts, and the summary the page now draws from them.
/// </summary>
public sealed class DesktopSalesDaySummaryTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 10);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public DesktopSalesDaySummaryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The screenshot, reproduced from the database up: three sales, each posted to SAP as its own
    /// invoice, so nothing in <c>SaleConsolidations</c> and <c>PostedInvoiceCount</c> at zero — while
    /// every one of the three is consolidated and the device holds nothing.
    /// </summary>
    [Fact]
    public async Task A_day_posted_one_invoice_at_a_time_reports_no_posted_invoices_and_no_unposted_sales()
    {
        AddSale("COR007", "TILL-20260910-0001", 7.22m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90001);
        AddSale("COR007", "TILL-20260910-0002", 7.22m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90002);
        AddSale("COR007", "TILL-20260910-0003", 7.22m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90003);
        await _context.SaveChangesAsync();

        var report = await BuildReportAsync();

        Assert.Equal(3, report.TotalSalesCount);
        Assert.Equal(21.66m, report.TotalSalesAmount);

        // The figure the consolidated lane used to read. It is not wrong — no consolidation document
        // was raised — it simply answers a different question from the other three lanes.
        Assert.Equal(0, report.PostedInvoiceCount);
        Assert.Equal(0, report.UnpostedInvoiceCount);
        Assert.Empty(report.UnpostedSales);
    }

    /// <summary>The card that day drew: three fiscalized, and four lanes of nothing.</summary>
    [Fact]
    public async Task The_day_card_counts_those_three_sales_as_consolidated()
    {
        AddSale("COR007", "TILL-20260910-0001", 7.22m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90001);
        AddSale("COR007", "TILL-20260910-0002", 7.22m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90002);
        AddSale("COR007", "TILL-20260910-0003", 7.22m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90003);
        await _context.SaveChangesAsync();

        var report = await MirrorAsync();
        var lanes = DesktopSalesDaySummary.Lanes(report);

        Assert.Equal(3, lanes.Sum(l => l.Count));
        Assert.Equal(3, Lane(lanes, "Consolidated"));
        Assert.Equal(0, Lane(lanes, "Awaiting close"));

        // And the card beside it no longer names a customer under a headline of zero.
        Assert.Equal(0m, DesktopSalesDaySummary.AwaitingValue(report));
        Assert.Empty(DesktopSalesDaySummary.OnDevice(report));
    }

    /// <summary>
    /// A day still in progress. The four lanes are the day, exactly — whatever the unposted list does
    /// not explain stays in the lane the Consolidate button acts on.
    /// </summary>
    [Fact]
    public async Task The_four_lanes_add_up_to_the_day_on_a_mixed_day()
    {
        AddSale("COR007", "TILL-20260910-0001", 10m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90001);
        AddSale("COR007", "TILL-20260910-0002", 20m, DesktopSaleConsolidationStatus.Pending);
        AddSale("SPA059", "TILL-20260910-0003", 30m, DesktopSaleConsolidationStatus.Pending);
        AddSale("SPA059", "TILL-20260910-0004", 40m, DesktopSaleConsolidationStatus.Failed);
        AddSale("KEF002", "TILL-20260910-0005", 50m, DesktopSaleConsolidationStatus.Excluded);
        await _context.SaveChangesAsync();

        var report = await MirrorAsync();
        var lanes = DesktopSalesDaySummary.Lanes(report);

        Assert.Equal(report.TotalSalesCount, lanes.Sum(l => l.Count));
        Assert.Equal(1, Lane(lanes, "Consolidated"));
        Assert.Equal(2, Lane(lanes, "Awaiting close"));
        Assert.Equal(1, Lane(lanes, "Failed"));
        Assert.Equal(1, Lane(lanes, "Excluded"));
    }

    /// <summary>
    /// The device card's breakdown is the headline's composition: the same rows, adding to the same
    /// figure, with the consolidated sale left out of both.
    /// </summary>
    [Fact]
    public async Task The_device_card_breaks_down_only_what_is_still_on_the_device()
    {
        AddSale("COR007", "TILL-20260910-0001", 10m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90001);
        AddSale("COR007", "TILL-20260910-0002", 20m, DesktopSaleConsolidationStatus.Pending);
        AddSale("SPA059", "TILL-20260910-0003", 30m, DesktopSaleConsolidationStatus.Pending);
        AddSale("SPA059", "TILL-20260910-0004", 40m, DesktopSaleConsolidationStatus.Failed);
        AddSale("KEF002", "TILL-20260910-0005", 50m, DesktopSaleConsolidationStatus.Excluded);
        await _context.SaveChangesAsync();

        var report = await MirrorAsync();
        var onDevice = DesktopSalesDaySummary.OnDevice(report);

        Assert.Equal(140m, DesktopSalesDaySummary.AwaitingValue(report));
        Assert.Equal(140m, onDevice.Sum(p => p.TotalAmount));

        Assert.Collection(onDevice,
            p => Assert.Equal(("SPA059", 70m), (p.CardCode, p.TotalAmount)),
            p => Assert.Equal(("KEF002", 50m), (p.CardCode, p.TotalAmount)),
            // 20, not the 30 COR007 sold: the consolidated sale is in the back office.
            p => Assert.Equal(("COR007", 20m), (p.CardCode, p.TotalAmount)));

        // The day-wide breakdown the card used to draw says something else entirely.
        Assert.Equal(
            30m,
            report.BusinessPartnerSummaries.Single(bp => bp.CardCode == "COR007").TotalAmount);
    }

    /// <summary>
    /// The card names the customer where the sale did, and falls back to the code where it did not — a
    /// code still says who, and a blank row would not.
    /// </summary>
    [Fact]
    public void A_customer_on_the_device_is_named_and_falls_back_to_its_code()
    {
        var named = new DesktopSalesDaySummary.PartnerValue("CIS006", "  Farm Counter Sales ", 10m);
        var unnamed = new DesktopSalesDaySummary.PartnerValue("CIS009", null, 5m);
        var blank = new DesktopSalesDaySummary.PartnerValue("CIS010", "   ", 5m);

        Assert.Equal("Farm Counter Sales", named.Label);
        Assert.Equal("Farm Counter Sales · CIS006", named.Title);
        Assert.Equal("CIS009", unnamed.Label);
        Assert.Equal("CIS009", unnamed.Title);
        Assert.Equal("CIS010", blank.Label);
    }

    // --- What was credited back ---
    //
    // The report totalled each sale at what it was rung up for even after the office had credited it
    // back, so the day card, the nightly email and the workbook all overstated the day.

    /// <summary>
    /// A credited sale stays in the day's count and takings at what it was rung up for; what was
    /// credited and what is left are stated beside them, on the customer and on the sale itself.
    /// </summary>
    [Fact]
    public async Task A_credited_sale_stays_in_the_takings_and_is_stated_as_a_correction()
    {
        AddSale("COR007", "TILL-20260910-0001", 100m, DesktopSaleConsolidationStatus.Consolidated, sapDocNum: 90001);
        AddSale("COR007", "TILL-20260910-0002", 40m, DesktopSaleConsolidationStatus.Pending);
        AddSale("SPA059", "TILL-20260910-0003", 30m, DesktopSaleConsolidationStatus.Pending);
        await _context.SaveChangesAsync();

        var partly = await SaleId("TILL-20260910-0001");
        var whole = await SaleId("TILL-20260910-0002");
        await SaleCreditSeed.TillCreditAsync(_context, partly, 25m);
        await SaleCreditSeed.TillCreditAsync(_context, whole, 40m);

        var report = await BuildReportAsync();

        Assert.Equal(3, report.TotalSalesCount);
        Assert.Equal(170m, report.TotalSalesAmount);
        Assert.Equal(65m, report.TotalCreditedAmount);
        Assert.Equal(105m, report.TotalNetOfCredits);
        Assert.Equal(2, report.CreditedSalesCount);
        Assert.Equal(1, report.FullyCreditedSalesCount);

        var customer = report.BusinessPartnerSummaries.Single(bp => bp.CardCode == "COR007");
        Assert.Equal(140m, customer.TotalAmount);
        Assert.Equal(65m, customer.CreditedAmount);
        Assert.Equal(75m, customer.NetOfCredits);

        var sale = customer.IndividualSales.Single(s => s.SaleId == partly);
        Assert.Equal(100m, sale.Amount);
        Assert.Equal(25m, sale.CreditedAmount);
        Assert.Equal([$"CN{partly}"], sale.CreditNoteNumbers);

        var untouched = report.BusinessPartnerSummaries.Single(bp => bp.CardCode == "SPA059");
        Assert.Equal(0m, untouched.CreditedAmount);
        Assert.Equal(30m, untouched.NetOfCredits);
        Assert.Empty(untouched.IndividualSales.Single().CreditNoteNumbers);
    }

    /// <summary>
    /// A credit comes off the day of the sale it reverses, not the day it was raised: the report for the
    /// sale's day changes, and no other day's does. The till and the van handset count it the same way.
    /// </summary>
    [Fact]
    public async Task A_credit_comes_off_the_day_of_its_sale_whenever_it_was_raised()
    {
        AddSale("COR007", "TILL-20260910-0001", 100m, DesktopSaleConsolidationStatus.Pending);
        AddSale("COR007", "TILL-20260911-0001", 60m, DesktopSaleConsolidationStatus.Pending, day: Day.AddDays(1));
        await _context.SaveChangesAsync();

        // SaleCreditSeed raises its credits on 25 September, a fortnight after either sale.
        await SaleCreditSeed.TillCreditAsync(_context, await SaleId("TILL-20260910-0001"), 25m);

        Assert.Equal(25m, (await BuildReportAsync()).TotalCreditedAmount);

        var nextDay = await new GenerateEndOfDayReportHandler(_context, new RecordingAuditService())
            .Handle(new GenerateEndOfDayReportQuery(Day.AddDays(1)), CancellationToken.None);
        Assert.Equal(0m, nextDay.Value.TotalCreditedAmount);
        Assert.Equal(60m, nextDay.Value.TotalNetOfCredits);
    }

    /// <summary>
    /// The page reads the report through a mirror written by hand. Sent as the API sends it and read as
    /// the page reads it, every credited figure arrives — a name that drifted would leave the card
    /// drawing a day with no credits.
    /// </summary>
    [Fact]
    public async Task The_credited_figures_reach_the_page_through_its_own_copy_of_the_report()
    {
        AddSale("COR007", "TILL-20260910-0001", 100m, DesktopSaleConsolidationStatus.Pending);
        await _context.SaveChangesAsync();
        await SaleCreditSeed.TillCreditAsync(_context, await SaleId("TILL-20260910-0001"), 25m);

        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var mirrored = System.Text.Json.JsonSerializer.Deserialize<WebDtos.EndOfDayReportDto>(
            System.Text.Json.JsonSerializer.Serialize(await BuildReportAsync(), web), web)!;

        Assert.Equal(100m, mirrored.TotalSalesAmount);
        Assert.Equal(25m, mirrored.TotalCreditedAmount);
        Assert.Equal(75m, mirrored.TotalNetOfCredits);
        Assert.Equal(1, mirrored.CreditedSalesCount);
        Assert.Equal(0, mirrored.FullyCreditedSalesCount);
        Assert.Equal(25m, mirrored.BusinessPartnerSummaries.Single().CreditedAmount);
        Assert.Equal(75m, mirrored.BusinessPartnerSummaries.Single().NetOfCredits);
    }

    // --- Harness ---

    private Task<int> SaleId(string reference) =>
        _context.DesktopSales.Where(s => s.ExternalReferenceId == reference).Select(s => s.Id).SingleAsync();

    private static int Lane(List<DesktopSalesDaySummary.Lane> lanes, string name) =>
        lanes.Single(l => l.Name == name).Count;

    private async Task<EndOfDayReportDto> BuildReportAsync()
    {
        var result = await new GenerateEndOfDayReportHandler(_context, new RecordingAuditService())
            .Handle(new GenerateEndOfDayReportQuery(Day), CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    /// <summary>
    /// The report as the page receives it. The Web mirrors the API's DTO by hand, so the summary is
    /// exercised against its own copy rather than the API's — the two are only equal by convention.
    /// </summary>
    private async Task<WebDtos.EndOfDayReportDto> MirrorAsync()
    {
        var report = await BuildReportAsync();

        return new WebDtos.EndOfDayReportDto
        {
            ReportDate = report.ReportDate,
            GeneratedAt = report.GeneratedAt,
            TotalSalesCount = report.TotalSalesCount,
            TotalSalesAmount = report.TotalSalesAmount,
            TotalVatAmount = report.TotalVatAmount,
            TotalAmountPaid = report.TotalAmountPaid,
            PostedInvoiceCount = report.PostedInvoiceCount,
            UnpostedInvoiceCount = report.UnpostedInvoiceCount,
            BusinessPartnerSummaries = report.BusinessPartnerSummaries
                .Select(bp => new WebDtos.BPSummaryDto
                {
                    CardCode = bp.CardCode,
                    CardName = bp.CardName,
                    SalesCount = bp.SalesCount,
                    TotalAmount = bp.TotalAmount,
                    TotalVat = bp.TotalVat,
                    TotalPaid = bp.TotalPaid
                })
                .ToList(),
            UnpostedSales = report.UnpostedSales
                .Select(s => new WebDtos.UnpostedSaleDto
                {
                    SaleId = s.SaleId,
                    ExternalReferenceId = s.ExternalReferenceId,
                    CardCode = s.CardCode,
                    CardName = s.CardName,
                    Amount = s.Amount,
                    FiscalReceiptNumber = s.FiscalReceiptNumber,
                    ConsolidationStatus = s.ConsolidationStatus,
                    Reason = s.Reason
                })
                .ToList()
        };
    }

    /// <summary>
    /// A sale on the day. Consolidated is written the way <c>DesktopSalePostingService</c> leaves it —
    /// the SAP invoice number on the sale itself and no <c>SaleConsolidation</c> row anywhere.
    /// </summary>
    private void AddSale(
        string cardCode,
        string reference,
        decimal total,
        DesktopSaleConsolidationStatus status,
        int? sapDocNum = null,
        DateTime? day = null)
    {
        var posted = status == DesktopSaleConsolidationStatus.Consolidated;

        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = cardCode,
            CardName = cardCode,
            DocDate = day ?? Day,
            NumAtCard = reference,
            TotalAmount = total,
            VatAmount = Math.Round(total * 15.5m / 115.5m, 2),
            AmountPaid = total,
            Currency = "USD",
            PaymentMethod = "Cash",
            WarehouseCode = "01",
            CreatedBy = "till",
            ConsolidationStatus = status,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            SapDocNum = posted ? sapDocNum : null,
            SapDocEntry = posted ? sapDocNum : null,
            PostedAt = posted ? Day.AddHours(9) : null,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheese 1kg",
                    Quantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = "01",
                    TaxId = 517,
                    TaxPercent = 15.5m
                }
            ]
        });
    }
}
