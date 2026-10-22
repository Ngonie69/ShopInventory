using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ShopInventory.Web.Features.Vending;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Depots &amp; vendors workbook: the figures checked against numbers worked out by hand, then the
/// file itself opened and read back.
/// </summary>
/// <remarks>
/// The fixture, in USD. Every vendor's items add up to their net (sales less VAT), as a vending till's do.
///
///   Graniteside (COR006): Tendai 3 invoices, 115.50 incl. VAT (100.00 net) — cheese 60, snacks 40
///                         Rudo   1 invoice,   57.75 (50.00 net)            — cheese 30, drinks 20
///                         Quiet  no sales now, sold 40 days ago
///                         Never  has never sold
///                         Gone   removed, sold 50.00 last period and nothing now
///   Bulawayo (COR008):    Farai  2 invoices, 231.00 (200.00 net)           — snacks 200
///
///   Last period: Tendai 100.00, Gone 50.00, snacks 120 net. Farai sold nothing.
///
///   Items: snacks 240 (68.6%), cheese 90 (25.7%), drinks 20 (5.7%) of 350 net.
/// </remarks>
public sealed class VendorPerformanceReportTests
{
    private const string Graniteside = "COR006";
    private const string Bulawayo = "COR008";

    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime To = new(2026, 10, 21);
    private static readonly DateTime Day = new(2026, 10, 20);

    private readonly ReportExportService _export = new();

    // ── The period it is compared with ─────────────────────────────────────

    [Fact]
    public void A_month_to_date_is_compared_with_the_same_days_of_the_month_before()
    {
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 21)),
            VendorPerformanceBuilder.PreviousWindow(From, To));
    }

    [Fact]
    public void A_whole_month_is_compared_with_the_whole_month_before_however_long()
    {
        Assert.Equal((new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)),
            VendorPerformanceBuilder.PreviousWindow(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31)));
    }

    [Fact]
    public void Any_other_window_is_compared_with_as_many_days_just_before_it()
    {
        Assert.Equal((new DateTime(2026, 10, 7), new DateTime(2026, 10, 13)),
            VendorPerformanceBuilder.PreviousWindow(new DateTime(2026, 10, 14), new DateTime(2026, 10, 20)));
    }

    // ── Vendors ────────────────────────────────────────────────────────────

    [Fact]
    public void Vendors_are_ranked_across_all_depots_and_within_their_own()
    {
        var report = Build();

        Assert.Equal(["Farai", "Tendai Moyo", "Rudo", "Quiet", "Never"], report.Vendors.Select(vendor => vendor.Name));
        Assert.Equal([1, 1, 2, 3, 4], report.Vendors.Select(vendor => vendor.RankInDepot));

        var tendai = report.Vendors.Single(vendor => vendor.Code == "VMP001");
        Assert.Equal(115.50m / 404.25m, tendai.ShareOfAll);
        Assert.Equal(115.50m / 173.25m, tendai.ShareOfDepot);
        Assert.Equal(115.50m / 3, tendai.AverageInvoice);
        Assert.Equal(100.00m, tendai.Net["USD"]);
        Assert.Equal(2, tendai.ItemCount);
        Assert.Equal("CHE011", tendai.TopItemCode);
    }

    [Fact]
    public void A_removed_vendor_with_nothing_in_the_period_is_left_off_the_list()
    {
        Assert.DoesNotContain(Build().Vendors, vendor => vendor.Code == "VMP009");
    }

    [Fact]
    public void A_vendor_who_has_sold_before_is_told_from_one_who_never_has()
    {
        var report = Build();

        Assert.Equal(VendorStanding.Quiet, report.Vendors.Single(vendor => vendor.Code == "VMP003").Standing);
        Assert.Equal(VendorStanding.NeverSold, report.Vendors.Single(vendor => vendor.Code == "VMP004").Standing);
        Assert.Equal(VendorStanding.Selling, report.Vendors.Single(vendor => vendor.Code == "VMP001").Standing);
    }

    [Fact]
    public void Change_is_against_the_previous_period_and_absent_for_a_vendor_new_to_selling()
    {
        var report = Build();

        Assert.Equal(0.155m, report.Vendors.Single(vendor => vendor.Code == "VMP001").Change);
        Assert.Null(report.Vendors.Single(vendor => vendor.Code == "VMB001").Change);
    }

    /// <summary>
    /// A depot's previous takings include vendors not listed now. Gone sold 50 last period and was removed;
    /// without them Graniteside's growth would read 73% instead of 15.5%.
    /// </summary>
    [Fact]
    public void A_depots_previous_takings_count_vendors_removed_since()
    {
        var graniteside = Build().Depots.Single(depot => depot.Code == Graniteside);

        Assert.Equal(150.00m, graniteside.PreviousGross["USD"]);
        Assert.Equal(0.155m, graniteside.Change);
    }

    [Fact]
    public void Depots_carry_their_own_totals_and_share()
    {
        var report = Build();
        var graniteside = report.Depots.Single(depot => depot.Code == Graniteside);

        Assert.Equal("Cortina Graniteside Vending", graniteside.Name);
        Assert.Equal(173.25m, graniteside.Gross["USD"]);
        Assert.Equal(150.00m, graniteside.Net["USD"]);
        Assert.Equal(4, graniteside.Invoices);
        Assert.Equal(2, graniteside.SellingVendors);
        Assert.Equal(173.25m / 404.25m, graniteside.Share);
        Assert.Equal(173.25m / 2, graniteside.AveragePerSellingVendor);

        Assert.Equal(404.25m, report.Totals.Gross["USD"]);
        Assert.Equal(6, report.Totals.Invoices);
        Assert.Equal(3, report.Totals.SellingVendors);
    }

    // ── Items ──────────────────────────────────────────────────────────────

    [Fact]
    public void Items_are_ranked_by_value_with_share_class_reach_and_change()
    {
        var products = Build().Products;

        Assert.Equal(["SNK002", "CHE011", "DRK003"], products.Select(item => item.ItemCode));

        var snacks = products[0];
        Assert.Equal(240m, snacks.Net["USD"]);
        Assert.Equal(60m, snacks.Quantity);
        Assert.Equal(240m / 350m, snacks.Share);
        Assert.Equal('A', snacks.Class);
        Assert.Equal(2, snacks.Vendors);
        Assert.Equal(2m / 3m, snacks.Reach);
        Assert.Equal(4m, snacks.AveragePrice);
        Assert.Equal(1m, snacks.Change);
        Assert.Equal(40m, snacks.NetByDepot[Graniteside]);
        Assert.Equal(200m, snacks.NetByDepot[Bulawayo]);

        // Cheese starts at 68.6% of sales, so it is still in the first 80%; drinks start at 94.3%.
        Assert.Equal(['A', 'A', 'B'], products.Select(item => item.Class));
        Assert.Equal(330m / 350m, products[1].CumulativeShare);
    }

    [Fact]
    public void A_depots_items_are_ranked_and_reached_within_the_depot()
    {
        var graniteside = Build().Depots.Single(depot => depot.Code == Graniteside);

        Assert.Equal(["CHE011", "SNK002", "DRK003"], graniteside.Products.Select(item => item.ItemCode));
        Assert.Equal(90m / 150m, graniteside.Products[0].Share);
        Assert.Equal(1m, graniteside.Products[0].Reach);
    }

    /// <summary>What the workbook tells its reader: a vendor's items add up to their net, to the cent.</summary>
    [Fact]
    public void Each_vendors_items_add_up_to_their_net_sales()
    {
        var report = Build();

        foreach (var vendor in report.Vendors.Where(vendor => vendor.Invoices > 0))
        {
            var items = report.VendorProducts.Where(item => item.VendorCode == vendor.Code).Sum(item => item.Net);
            Assert.Equal(vendor.Net["USD"], items);
        }
    }

    // ── Currencies and scope ───────────────────────────────────────────────

    [Fact]
    public void A_second_currency_keeps_its_own_figures_and_never_joins_the_first()
    {
        var sales = Sales();
        sales.Single(row => row.Code == "VMB001").TotalsByCurrency.Add(Totals("ZWG", 1, 2_000m, 0m));
        var items = Items();
        items.Add(Item(Bulawayo, 3, "VMB001", "SNK002", 10m, 2_000m, "ZWG"));

        var report = Build(sales: sales, items: items);

        // ZWG 2,000 is the bigger number and the smaller trade: one invoice against six.
        Assert.Equal(["USD", "ZWG"], report.Currencies);
        Assert.Equal("USD", report.Currency);
        Assert.Equal(404.25m, report.Totals.Gross["USD"]);
        Assert.Equal(2_000m, report.Totals.Gross["ZWG"]);
        // The share is read in USD alone; the ZWG sale moves nothing.
        Assert.Equal(240m / 350m, report.Products[0].Share);
        Assert.Equal(4m, report.Products[0].AveragePrice);
    }

    [Fact]
    public void Naming_a_depot_reports_on_that_depot_alone()
    {
        var report = Build(depotFilter: Bulawayo);

        Assert.Equal("Cortina Bulawayo Vending", report.ScopeLabel);
        Assert.Equal(Bulawayo, Assert.Single(report.Depots).Code);
        Assert.Equal("VMB001", Assert.Single(report.Vendors).Code);
        Assert.Equal("SNK002", Assert.Single(report.Products).ItemCode);
    }

    // ── The workbook ───────────────────────────────────────────────────────

    [Fact]
    public void The_workbook_has_a_summary_a_sheet_per_depot_and_the_three_registers()
    {
        using var workbook = Open(_export.ExportVendorPerformanceToExcel(Build()));

        Assert.Equal(
            ["Summary", "Cortina Graniteside Vending", "Cortina Bulawayo Vending", "All vendors", "Products", "Vendor products"],
            workbook.Worksheets.Select(sheet => sheet.Name));
    }

    [Fact]
    public void The_depot_sheet_lists_its_vendors_best_first_and_totals_them()
    {
        using var workbook = Open(_export.ExportVendorPerformanceToExcel(Build()));
        var sheet = workbook.Worksheet("Cortina Graniteside Vending");

        var header = HeaderCell(sheet, "Vendor");
        Assert.Equal("Tendai Moyo", sheet.Cell(header.Address.RowNumber + 1, header.Address.ColumnNumber).GetString());
        Assert.Equal("Rudo", sheet.Cell(header.Address.RowNumber + 2, header.Address.ColumnNumber).GetString());

        var sales = HeaderCell(sheet, "Sales USD (incl. VAT)");
        var first = sales.Address.RowNumber + 1;
        Assert.Equal(115.50, sheet.Cell(first, sales.Address.ColumnNumber).GetDouble());

        // Four vendors listed, then the totals row, which follows the filter.
        var total = sheet.Cell(first + 4, sales.Address.ColumnNumber);
        Assert.Equal($"SUBTOTAL(109,{total.Address.ColumnLetter}{first}:{total.Address.ColumnLetter}{first + 3})", total.FormulaA1);
    }

    [Fact]
    public void The_products_sheet_ranks_items_with_their_class_and_each_depots_value()
    {
        using var workbook = Open(_export.ExportVendorPerformanceToExcel(Build()));
        var sheet = workbook.Worksheet("Products");

        var item = HeaderCell(sheet, "Item");
        var row = item.Address.RowNumber + 1;
        Assert.Equal("SNK002", sheet.Cell(row, item.Address.ColumnNumber).GetString());
        Assert.Equal("A", sheet.Cell(row, HeaderCell(sheet, "Class").Address.ColumnNumber).GetString());
        Assert.Equal(240.0 / 350.0, sheet.Cell(row, HeaderCell(sheet, "Share").Address.ColumnNumber).GetDouble(), 6);
        Assert.Equal(1.0, sheet.Cell(row, HeaderCell(sheet, "Change").Address.ColumnNumber).GetDouble(), 6);
        Assert.Equal(200.0, sheet.Cell(row, HeaderCell(sheet, "Cortina Bulawayo Vending (USD)").Address.ColumnNumber).GetDouble());
    }

    [Fact]
    public void A_vendor_new_to_selling_reads_New_rather_than_a_blank_change()
    {
        using var workbook = Open(_export.ExportVendorPerformanceToExcel(Build()));
        var sheet = workbook.Worksheet("All vendors");

        var code = HeaderCell(sheet, "Code");
        var farai = sheet.Column(code.Address.ColumnNumber).CellsUsed().First(cell => cell.GetString() == "VMB001");
        Assert.Equal("New", sheet.Cell(farai.Address.RowNumber, HeaderCell(sheet, "Change").Address.ColumnNumber).GetString());
    }

    [Fact]
    public void A_second_currency_gets_its_own_columns()
    {
        var sales = Sales();
        sales.Single(row => row.Code == "VMB001").TotalsByCurrency.Add(Totals("ZWG", 1, 2_000m, 0m));

        using var workbook = Open(_export.ExportVendorPerformanceToExcel(Build(sales: sales)));
        var sheet = workbook.Worksheet("All vendors");

        Assert.NotNull(HeaderCell(sheet, "Sales USD (incl. VAT)"));
        Assert.NotNull(HeaderCell(sheet, "Sales ZWG (incl. VAT)"));
    }

    /// <summary>
    /// The charts are added to the saved file by hand, where a misplaced element makes Excel "repair" the
    /// file and drop them. The validator is what catches that short of opening Excel.
    /// </summary>
    [Fact]
    public void The_file_is_valid_and_the_summary_carries_both_charts()
    {
        var bytes = _export.ExportVendorPerformanceToExcel(Build());

        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(error => $"{error.Path?.XPath}: {error.Description}")));

        var summary = SheetPart(document, "Summary");
        Assert.Equal(2, summary.DrawingsPart?.ChartParts.Count() ?? 0);
    }

    [Fact]
    public void A_period_with_no_sales_still_makes_a_valid_file_without_charts()
    {
        var bytes = _export.ExportVendorPerformanceToExcel(Build(sales: [], previous: [], items: [], previousItems: []));

        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document));
        Assert.Null(SheetPart(document, "Summary").DrawingsPart);
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    private static VendorPerformanceReport Build(
        List<RouteCustomerSalesRowModel>? sales = null,
        List<RouteCustomerSalesRowModel>? previous = null,
        List<RouteCustomerProductSalesRowModel>? items = null,
        List<RouteCustomerProductSalesRowModel>? previousItems = null,
        string? depotFilter = null) =>
        VendorPerformanceBuilder.Build(new VendorPerformanceSources(
            Overview(),
            new Dictionary<string, string>
            {
                [Graniteside] = "Cortina Graniteside Vending",
                [Bulawayo] = "Cortina Bulawayo Vending",
            },
            depotFilter,
            From,
            To,
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 21),
            sales ?? Sales(),
            previous ?? PreviousSales(),
            items ?? Items(),
            previousItems ?? [Item(Bulawayo, 3, "VMB001", "SNK002", 30m, 120m)]));

    private static VendingOverviewModel Overview() => new()
    {
        Depots =
        [
            new() { BusinessPartnerCode = Graniteside, WarehouseCodes = ["KEFGRC"], ActiveCashierCount = 2, ActiveVendorCount = 4 },
            new() { BusinessPartnerCode = Bulawayo, WarehouseCodes = ["KEFBYC"], ActiveCashierCount = 1, ActiveVendorCount = 1 },
        ],
        Vendors =
        [
            new() { Id = 1, Code = "VMP001", Name = "Tendai", Surname = "Moyo", Phone = "0771000001", AssignedBusinessPartnerCode = Graniteside, IsActive = true },
            new() { Id = 2, Code = "VMP002", Name = "Rudo", AssignedBusinessPartnerCode = Graniteside, IsActive = true },
            new() { Id = 3, Code = "VMB001", Name = "Farai", AssignedBusinessPartnerCode = Bulawayo, IsActive = true },
            new() { Id = 4, Code = "VMP003", Name = "Quiet", AssignedBusinessPartnerCode = Graniteside, IsActive = true },
            new() { Id = 5, Code = "VMP004", Name = "Never", AssignedBusinessPartnerCode = Graniteside, IsActive = true },
            new() { Id = 9, Code = "VMP009", Name = "Gone", AssignedBusinessPartnerCode = Graniteside, IsActive = false },
        ],
    };

    private static List<RouteCustomerSalesRowModel> Sales() =>
    [
        Row(1, "VMP001", "Tendai", Graniteside, 3, 5, Day, Totals("USD", 3, 115.50m, 15.50m)),
        Row(2, "VMP002", "Rudo", Graniteside, 1, 2, Day, Totals("USD", 1, 57.75m, 7.75m)),
        Row(3, "VMB001", "Farai", Bulawayo, 2, 2, Day, Totals("USD", 2, 231.00m, 31.00m)),
        Row(4, "VMP003", "Quiet", Graniteside, 0, 0, Day.AddDays(-40)),
        Row(5, "VMP004", "Never", Graniteside, 0, 0, null),
        Row(9, "VMP009", "Gone", Graniteside, 0, 0, Day.AddDays(-35), active: false),
    ];

    private static List<RouteCustomerSalesRowModel> PreviousSales() =>
    [
        Row(1, "VMP001", "Tendai", Graniteside, 2, 3, Day.AddDays(-30), Totals("USD", 2, 100m, 13.42m)),
        Row(9, "VMP009", "Gone", Graniteside, 1, 1, Day.AddDays(-35), Totals("USD", 1, 50m, 6.71m), active: false),
    ];

    private static List<RouteCustomerProductSalesRowModel> Items() =>
    [
        Item(Graniteside, 1, "VMP001", "CHE011", 20m, 60m),
        Item(Graniteside, 1, "VMP001", "SNK002", 10m, 40m),
        Item(Graniteside, 2, "VMP002", "CHE011", 10m, 30m),
        Item(Graniteside, 2, "VMP002", "DRK003", 5m, 20m),
        Item(Bulawayo, 3, "VMB001", "SNK002", 50m, 200m),
    ];

    private static RouteCustomerSalesRowModel Row(
        int id, string code, string name, string depot, int sales, int lines, DateTime? lastSale,
        RouteCustomerSalesTotalsModel? totals = null, bool active = true) => new()
        {
            RouteCustomerId = id,
            Code = code,
            Name = name,
            AssignedBusinessPartnerCode = depot,
            IsActive = active,
            SaleCount = sales,
            LineCount = lines,
            LastSaleAt = lastSale,
            DaysSinceLastSale = lastSale is { } day ? (To - day).Days : null,
            TotalsByCurrency = totals is null ? [] : [totals],
        };

    private static RouteCustomerSalesTotalsModel Totals(string currency, int count, decimal gross, decimal vat) =>
        new() { Currency = currency, SaleCount = count, Gross = gross, Vat = vat, AmountPaid = gross };

    private static RouteCustomerProductSalesRowModel Item(
        string depot, int vendorId, string vendorCode, string item, decimal quantity, decimal total, string currency = "USD") => new()
        {
            AssignedBusinessPartnerCode = depot,
            RouteCustomerId = vendorId,
            RouteCustomerCode = vendorCode,
            ItemCode = item,
            ItemDescription = item switch { "CHE011" => "Cheese puffs", "SNK002" => "Snack bar", _ => "Drink 500ml" },
            UoMCode = "EA",
            Currency = currency,
            Quantity = quantity,
            LineTotal = total,
            LineCount = 1,
            LastSoldAt = Day,
        };

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static IXLCell HeaderCell(IXLWorksheet sheet, string header) =>
        sheet.CellsUsed().First(cell => cell.GetString() == header);

    private static WorksheetPart SheetPart(SpreadsheetDocument document, string name)
    {
        var sheet = document.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .Single(candidate => candidate.Name == name);
        return (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!);
    }
}
