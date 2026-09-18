using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the desktop sales business review: that its per-shop figures add back to the analysis the till
/// page shows, and that each finding is written when — and only when — the figures call for it.
/// </summary>
/// <remarks>
/// The analysis and the management report are run for real behind a stub mediator, so what is tested is
/// the review as it is composed in production, on SQLite so every grouping has to translate.
/// </remarks>
public sealed class DesktopSalesReviewTests : IDisposable
{
    // A four-day period, 14–17 Sep, and the four days before it.
    private static readonly DateTime From = new(2026, 9, 14);
    private static readonly DateTime To = new(2026, 9, 17);

    private const string FactoryShop = "KEFSHOP";
    private const string SmallShop = "KEFGRS";
    private const string VendingCentre = "KEFBYC";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _factoryCashierId = Guid.NewGuid();
    private readonly Guid _smallCashierId = Guid.NewGuid();
    private readonly Guid _confinedId = Guid.NewGuid();
    private int _reference;

    public DesktopSalesReviewTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        var factory = new ShopEntity { Code = "FACTORY", Name = "Kefalos Factory Shop", BusinessPartnerCode = "KEFSHOP-BP", WarehouseCode = FactoryShop, IsActive = true };
        var small = new ShopEntity { Code = "GRANITE", Name = "Kefalos Graniteside Shop", BusinessPartnerCode = "KEFGRS-BP", WarehouseCode = SmallShop, IsActive = true };
        _context.Shops.AddRange(factory, small);
        _context.SaveChanges();

        _context.Users.AddRange(
            new User { Id = _adminId, Username = "admin", PasswordHash = "x", Role = ApplicationRoles.Admin, IsActive = true },
            new User { Id = _factoryCashierId, Username = "factory", PasswordHash = "x", Role = ApplicationRoles.Cashier, IsActive = true },
            new User { Id = _smallCashierId, Username = "granite", PasswordHash = "x", Role = ApplicationRoles.Cashier, IsActive = true },
            new User { Id = _confinedId, Username = "granite-till", PasswordHash = "x", Role = ApplicationRoles.Cashier, IsActive = true, ShopId = small.Id });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- The figures ------------------------------------------------------------------------------

    [Fact]
    public async Task Shop_rows_add_back_to_the_analysis_headline()
    {
        SeedWeek();

        var usd = Dollars(await RunAsync());

        Assert.Equal(usd.Headline.TotalAmount, usd.ByShop.Sum(s => s.TotalAmount));
        Assert.Equal(usd.Headline.SalesCount, usd.ByShop.Sum(s => s.SalesCount));
        Assert.Equal(usd.Headline.TotalAmount, usd.ShopDays.Sum(d => d.TotalAmount));
        Assert.Equal(usd.Headline.SalesCount, usd.ByHour.Sum(h => h.SalesCount));
        Assert.Equal(usd.Headline.QuantitySold, usd.ByShop.Sum(s => s.QuantitySold));
        Assert.Equal(usd.Headline.ShortTendered, usd.ByShop.Sum(s => s.ShortTendered));
    }

    [Fact]
    public async Task Vending_settlements_are_counted_apart_in_the_hours()
    {
        SeedWeek();

        var usd = Dollars(await RunAsync());
        var vending = usd.ByShop.Single(s => s.WarehouseCode == VendingCentre);

        Assert.Equal("Vending", vending.Channel);
        Assert.Equal(vending.SalesCount, usd.ByHour.Sum(h => h.SettlementSalesCount));
        Assert.Equal(vending.TotalAmount, usd.ByHour.Sum(h => h.SettlementTotalAmount));
    }

    [Fact]
    public async Task A_shop_whose_first_sale_is_inside_the_period_is_marked_as_started()
    {
        SeedWeek();

        var usd = Dollars(await RunAsync());

        var vending = usd.ByShop.Single(s => s.WarehouseCode == VendingCentre);
        Assert.True(vending.StartedInPeriod);
        Assert.Equal(To, vending.FirstSaleDate);
        Assert.Equal(1, vending.DaysTraded);
        Assert.Equal(vending.TotalAmount, vending.PerTradingDay);

        // Traded in the previous period, so its total covers the whole of this one.
        Assert.False(usd.ByShop.Single(s => s.WarehouseCode == SmallShop).StartedInPeriod);
    }

    [Fact]
    public async Task Traffic_and_ticket_add_up_to_the_change_in_takings()
    {
        SeedWeek();

        var headline = Dollars(await RunAsync()).Headline;

        Assert.NotNull(headline.TrafficEffect);
        Assert.NotNull(headline.TicketEffect);
        Assert.Equal(
            headline.TotalAmount - headline.PreviousTotalAmount,
            headline.TrafficEffect!.Value + headline.TicketEffect!.Value,
            precision: 1);
    }

    [Fact]
    public async Task Short_tender_counts_only_a_recorded_tender_that_fell_short()
    {
        Add(Sale(10.00m, From, FactoryShop, paid: 9.80m));
        Add(Sale(10.00m, From, FactoryShop, paid: 0m));       // no tender recorded: not short
        Add(Sale(10.00m, From, FactoryShop, paid: 20.00m));   // overpaid, with change: not short

        var usd = Dollars(await RunAsync());

        Assert.Equal(0.20m, usd.Headline.ShortTendered);
        Assert.Equal(0.20m, usd.ByShop.Single().ShortTendered);
    }

    [Fact]
    public async Task An_item_priced_differently_between_shops_is_listed_with_the_money_at_stake()
    {
        Add(Sale(62.70m, From, FactoryShop, lines: [("TUB5L", 10, 62.70m)]));
        Add(Sale(76.20m, From, SmallShop, lines: [("TUB5L", 10, 76.20m)]));

        var usd = Dollars(await RunAsync());
        var spread = Assert.Single(usd.PriceSpreads);

        Assert.Equal("TUB5L", spread.ItemCode);
        Assert.Equal(FactoryShop, spread.LowWarehouseCode);
        Assert.Equal(6.27m, spread.LowUnitPrice);
        Assert.Equal(7.62m, spread.HighUnitPrice);
        Assert.Equal(21.5m, spread.SpreadPercent);
        Assert.Equal(13.50m, spread.UpliftAtHighPrice);
    }

    [Fact]
    public async Task A_vendor_trade_price_is_not_set_against_a_counter_retail_price()
    {
        Add(Sale(36m, From, VendingCentre, source: SaleSourceSystems.Vending, lines: [("CONE", 100, 36m)]));
        Add(Sale(100m, From, FactoryShop, lines: [("CONE", 100, 100m)]));

        Assert.Empty(Dollars(await RunAsync()).PriceSpreads);
    }

    [Fact]
    public async Task A_trade_size_sale_is_listed_and_a_named_customer_is_told_apart_from_a_walk_in()
    {
        for (var i = 0; i < 20; i++)
        {
            Add(Sale(10m, From, SmallShop));
        }

        Add(Sale(411.48m, From, SmallShop));
        Add(Sale(400m, To, SmallShop, cardCode: "TRADE001"));

        var usd = Dollars(await RunAsync());

        Assert.Equal(2, usd.LargeSales.Count);
        Assert.Null(usd.LargeSales.Single(s => s.TotalAmount == 411.48m).CustomerCode);
        Assert.Equal("TRADE001", usd.LargeSales.Single(s => s.TotalAmount == 400m).CustomerCode);
    }

    [Fact]
    public async Task The_best_sellers_carry_a_cumulative_share_and_a_Pareto_class()
    {
        Add(Sale(90m, From, FactoryShop, lines: [("A", 1, 90m)]));
        Add(Sale(6m, From, FactoryShop, lines: [("B", 1, 6m)]));
        Add(Sale(4m, From, FactoryShop, lines: [("C", 1, 4m)]));

        var items = Dollars(await RunAsync()).TopItems;

        Assert.Equal(["A", "B", "C"], items.Select(i => i.ItemCode));
        Assert.Equal([90m, 96m, 100m], items.Select(i => i.CumulativeSharePercent));
        Assert.Equal(["B", "C", "C"], items.Select(i => i.AbcClass));
    }

    [Fact]
    public async Task A_confined_caller_cannot_review_another_shop()
    {
        SeedWeek();

        var result = await HandleAsync(new GetDesktopSalesReviewQuery(_confinedId, From, To, FactoryShop));

        Assert.True(result.IsError);
    }

    // ---- The findings -----------------------------------------------------------------------------

    [Fact]
    public async Task The_week_writes_the_findings_its_figures_call_for()
    {
        SeedWeek();

        var review = await RunAsync();
        var codes = review.Findings.Select(f => f.Code).ToList();

        Assert.Contains("shops-started", codes);
        Assert.Contains("takings-change", codes);
        Assert.Contains("vending-settlements", codes);
        Assert.Contains("cash-heavy", codes);
        Assert.Contains("short-tender", codes);
        Assert.Contains("price-spread", codes);
        Assert.DoesNotContain("high-change", codes);
        Assert.DoesNotContain("no-comparison", codes);
        Assert.DoesNotContain("missing-reference", codes);

        // Most urgent first.
        var ranks = review.Findings.Select(f => DesktopSalesReviewSeverity.Rank(f.Severity)).ToList();
        Assert.Equal(ranks.OrderBy(r => r), ranks);

        var started = review.Findings.Single(f => f.Code == "shops-started");
        Assert.Contains(VendingCentre, started.Title + started.Detail);
    }

    [Fact]
    public async Task A_first_period_says_there_is_nothing_to_compare_with()
    {
        Add(Sale(10m, From, FactoryShop));

        var codes = (await RunAsync()).Findings.Select(f => f.Code).ToList();

        Assert.Contains("no-comparison", codes);
        Assert.DoesNotContain("takings-change", codes);
    }

    [Fact]
    public async Task A_mobile_money_sale_without_a_reference_is_an_action()
    {
        var sale = Sale(5m, From, FactoryShop);
        sale.PaymentMethod = TenderTypes.Ecocash;
        sale.PaymentReference = null;
        Add(sale);

        var finding = Assert.Single((await RunAsync()).Findings, f => f.Code == "missing-reference");

        Assert.Equal(DesktopSalesReviewSeverity.Action, finding.Severity);
    }

    [Fact]
    public async Task A_failed_fiscalisation_leads_the_findings()
    {
        SeedWeek();
        var sale = Sale(25m, To, FactoryShop);
        sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Failed;
        Add(sale);

        var first = (await RunAsync()).Findings[0];

        Assert.Equal("fiscal-failed", first.Code);
        Assert.Equal(DesktopSalesReviewSeverity.Action, first.Severity);
    }

    // ---- The document -----------------------------------------------------------------------------

    [Fact]
    public async Task The_pdf_carries_the_findings_and_every_shop()
    {
        SeedWeek();
        var review = await RunAsync();

        var document = new GetDesktopSalesReviewPdfHandler(StubProxy.For<IMediator>((_, args) =>
                args?[0] is GetDesktopSalesReviewQuery ? Task.FromResult<ErrorOr<DesktopSalesReview>>(review) : null))
            .Handle(new GetDesktopSalesReviewPdfQuery(_adminId, From, To, Title: "Weekly review"), CancellationToken.None);

        var pdf = (await document).Value;
        Assert.Equal("Desktop_Sales_Review_20260914_20260917.pdf", pdf.FileName);

        var text = PdfText(pdf.Content);
        Assert.Contains("Weekly review", text);
        Assert.Contains("Kefalos Factory Shop", text);
        Assert.Contains(VendingCentre, text);
        foreach (var finding in review.Findings)
        {
            // Titles may wrap, so the first few words are enough to find each one.
            Assert.Contains(string.Join(' ', finding.Title.Replace("→", "to").Replace("−", "-").Split(' ').Take(3)), text);
        }

        // Opt-in, for looking at the real rendered document and for feeding the page a real review.
        var outPath = Environment.GetEnvironmentVariable("DESKTOP_SALES_REVIEW_PDF_OUT");
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            await File.WriteAllBytesAsync(outPath, pdf.Content);
            await File.WriteAllTextAsync(
                Path.ChangeExtension(outPath, ".json"),
                System.Text.Json.JsonSerializer.Serialize(review, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        }
    }

    private static string PdfText(byte[] content)
    {
        using var reader = new iText.Kernel.Pdf.PdfReader(new MemoryStream(content));
        using var pdf = new iText.Kernel.Pdf.PdfDocument(reader);
        return string.Join(' ', Enumerable.Range(1, pdf.GetNumberOfPages())
            .Select(page => iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor.GetTextFromPage(pdf.GetPage(page))))
            .ReplaceLineEndings(" ");
    }

    // ---- Seeding ----------------------------------------------------------------------------------

    /// <summary>
    /// The shape of the real rollout week: a factory shop selling tubs, a small till selling snacks
    /// with big notes, and a vending centre that pays in its rounds on the last day only.
    /// </summary>
    private void SeedWeek()
    {
        // Previous period: the two tills.
        Add(Sale(300m, From.AddDays(-3), FactoryShop, lines: [("TUB5L", 48, 300m)]));
        Add(Sale(40m, From.AddDays(-2), SmallShop, paid: 50m, lines: [("CONE", 40, 40m)]));

        // This period.
        foreach (var day in Enumerable.Range(0, 4).Select(d => From.AddDays(d)))
        {
            Add(Sale(62.70m, day, FactoryShop, lines: [("TUB5L", 10, 62.70m)]));
            Add(Sale(20m, day, FactoryShop, paid: 20m, method: TenderTypes.Swipe, lines: [("CONE", 20, 20m)]));

            for (var i = 0; i < 6; i++)
            {
                Add(Sale(7.62m, day, SmallShop, paid: 10m, lines: [("TUB5L", 1, 7.62m)]));
            }
        }

        Add(Sale(9.90m, To, SmallShop, paid: 9.50m, lines: [("CONE", 10, 9.90m)]));

        for (var i = 0; i < 5; i++)
        {
            Add(Sale(43m, To, VendingCentre, source: SaleSourceSystems.Vending, lines: [("CONE", 120, 43m)], hour: 10));
        }
    }

    private void Add(DesktopSaleEntity sale)
    {
        _context.DesktopSales.Add(sale);
        _context.SaveChanges();
    }

    private DesktopSaleEntity Sale(
        decimal total,
        DateTime day,
        string warehouse,
        decimal? paid = null,
        string method = TenderTypes.Cash,
        string source = SaleSourceSystems.ShopTill,
        string? cardCode = null,
        (string Code, int Quantity, decimal Net)[]? lines = null,
        int hour = 12) => new()
        {
            ExternalReferenceId = $"REF-{++_reference}",
            SourceSystem = source,
            CardCode = cardCode ?? $"{warehouse}-BP",
            WarehouseCode = warehouse,
            DocDate = day,
            TotalAmount = total,
            VatAmount = Math.Round(total * 0.155m / 1.155m, 2),
            AmountPaid = paid ?? total,
            Currency = "USD",
            PaymentMethod = method,
            PaymentReference = "REF",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            CreatedBy = (warehouse == SmallShop ? _smallCashierId : _factoryCashierId).ToString(),

            // 10:00 UTC is 12:00 CAT.
            CreatedAt = DateTime.SpecifyKind(day.AddHours(hour - 2), DateTimeKind.Utc),
            Lines = (lines ?? [("MISC", 1, Math.Round(total / 1.155m, 2))])
                .Select((line, index) => new DesktopSaleLineEntity
                {
                    LineNum = index + 1,
                    ItemCode = line.Code,
                    ItemDescription = line.Code,
                    Quantity = line.Quantity,
                    UnitPrice = Math.Round(line.Net / line.Quantity, 4),
                    LineTotal = line.Net,
                    WarehouseCode = warehouse,
                })
                .ToList(),
        };

    // ---- Running ----------------------------------------------------------------------------------

    private async Task<DesktopSalesReview> RunAsync()
    {
        var result = await HandleAsync(new GetDesktopSalesReviewQuery(_adminId, From, To));
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<ErrorOr<DesktopSalesReview>> HandleAsync(GetDesktopSalesReviewQuery query)
    {
        _context.ChangeTracker.Clear();
        return await new GetDesktopSalesReviewHandler(_context, Mediator()).Handle(query, CancellationToken.None);
    }

    /// <summary>The two reports the review reads, run for real.</summary>
    private IMediator Mediator() =>
        StubProxy.For<IMediator>((method, args) => args?[0] switch
        {
            GetDesktopSalesAnalysisQuery analysis when method.Name == nameof(IMediator.Send) =>
                new GetDesktopSalesAnalysisHandler(_context, new RecordingAuditService())
                    .Handle(analysis, CancellationToken.None),
            GetManagementSalesReportQuery management when method.Name == nameof(IMediator.Send) =>
                new GetManagementSalesReportHandler(
                        _context, new RecordingAuditService(), new NoCost(), NullLogger<GetManagementSalesReportHandler>.Instance)
                    .Handle(management, CancellationToken.None),
            _ => throw new InvalidOperationException($"Unexpected mediator call: {method.Name}")
        });

    private static DesktopSalesReviewCurrency Dollars(DesktopSalesReview review) =>
        review.Currencies.Single(c => c.Currency == "USD");

    private sealed class NoCost : ISaleInvoiceCostReader
    {
        public Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
            IReadOnlyCollection<string> warehouseCodes, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SaleInvoiceLineCost>>([]);
    }
}
