using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;
using ShopInventory.Web.Features.RouteCustomers;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace ShopInventory.Tests;

/// <summary>
/// The Route Sales Report's overview rankings and its detail workbook: the rankings checked against the
/// fixture by hand, then the file opened, validated and read back.
/// </summary>
/// <remarks>
/// The fixture. Dormant after 30 days.
///
///   VAN001  Alpha    USD 500 (5 sales), ZWG 9,000 (2)   last sale 2 days ago
///           Bravo    USD 800 (6 sales)                  last sale 1 day ago
///           Charlie  never bought
///           Delta    nothing in the period              last sale 45 days ago — dormant
///   VAN002  Echo     USD 300 (3 sales)                  last sale 3 days ago
///           Foxtrot  ZWG 20,000 (4 sales)               last sale 5 days ago
///
///   USD has 14 sales and ZWG 6, so USD is the currency the overview opens on.
///   USD ranks Bravo, Alpha, Echo; ZWG ranks Foxtrot, Alpha. VAN001 is the larger route in USD, VAN002 in ZWG.
///   Items: milk 12 times by 4 customers, yoghurt 12 times by 2, cheese 10 times — milk, yoghurt, cheese.
/// </remarks>
public sealed class RouteSalesDetailWorkbookTests
{
    private static readonly DateTime From = new(2026, 8, 1);
    private static readonly DateTime To = new(2026, 10, 29);

    private readonly ReportExportService _export = new();

    // ── Overview ───────────────────────────────────────────────────────────

    [Fact]
    public void The_busiest_currency_comes_first()
    {
        Assert.Equal(["USD", "ZWG"], RouteSalesOverview.Currencies(Summary()));
    }

    [Fact]
    public void Customers_are_ranked_in_one_currency_at_a_time()
    {
        var customers = Summary().Routes.SelectMany(route => route.Customers).ToList();

        var usd = RouteSalesOverview.TopCustomers(customers, "USD");
        Assert.Equal(["Bravo", "Alpha", "Echo"], usd.Select(entry => entry.Customer.Name));
        Assert.Equal([1, 2, 3], usd.Select(entry => entry.Rank));
        Assert.Equal(1m, usd[0].Share);
        Assert.Equal(500m / 800m, usd[1].Share);

        // Alpha's ZWG is ranked against ZWG alone — never added to its USD.
        var zwg = RouteSalesOverview.TopCustomers(customers, "ZWG");
        Assert.Equal(["Foxtrot", "Alpha"], zwg.Select(entry => entry.Customer.Name));
        Assert.Equal(9_000m, zwg[1].Totals.Gross);
    }

    [Fact]
    public void Routes_are_ordered_by_the_chosen_currency()
    {
        var summary = Summary();

        Assert.Equal(["VAN001", "VAN002"], RouteSalesOverview.Routes(summary, "USD").Select(line => line.Route.AssignedBusinessPartnerCode));
        Assert.Equal(["VAN002", "VAN001"], RouteSalesOverview.Routes(summary, "ZWG").Select(line => line.Route.AssignedBusinessPartnerCode));

        var van001 = RouteSalesOverview.Routes(summary, "USD")[0];
        Assert.Equal(["Bravo", "Alpha"], van001.TopCustomers.Select(entry => entry.Customer.Name));
        Assert.Equal(2, van001.TradingCount);
        Assert.Equal(1_600m, RouteSalesOverview.Total(summary, "USD")!.Gross);
    }

    [Fact]
    public void Products_rank_by_times_bought_then_by_customers()
    {
        var top = RouteSalesOverview.TopProducts(Mix());

        Assert.Equal(["MLK", "YOG", "CHE"], top.Select(entry => entry.Item.ItemCode));
    }

    [Fact]
    public void The_visit_list_is_the_dormant_then_the_never_bought()
    {
        Assert.Equal(["Delta", "Charlie"], RouteSalesOverview.NeedsAVisit(Summary()).Select(customer => customer.Name));
    }

    [Fact]
    public void Two_totals_in_one_currency_are_added_and_keep_the_costed_margin()
    {
        var combined = RouteSalesOverview.In(
            [Totals("USD", 1, 100m, gp: 20m, net: 80m), Totals("usd", 1, 50m, gp: null, net: null)], "USD")!;

        Assert.Equal((150m, 2, 20m, 80m, 25m), (combined.Gross, combined.SaleCount, combined.GrossProfit!.Value, combined.CostedNetAmount!.Value, combined.MarginPercent!.Value));
    }

    // ── The workbook ───────────────────────────────────────────────────────

    /// <summary>
    /// The charts are written into the saved file by hand, where a misplaced element makes Excel "repair"
    /// the file and drop them. The validator is what catches that short of opening Excel.
    /// </summary>
    [Fact]
    public void The_workbook_is_valid_with_seven_sheets_and_five_charts()
    {
        var bytes = _export.ExportRouteSalesDetailToExcel(Summary(), Mix(), Lines(), Labels());

        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(error => $"{error.Path?.XPath}: {error.Description}")));

        var names = document.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(sheet => sheet.Name!.Value);
        Assert.Equal(["Summary", "Charts", "Routes", "Customers", "Customer products", "Products", "Dormant"], names);

        var charts = SheetPart(document, "Charts").DrawingsPart!.ChartParts.ToList();
        Assert.Equal(5, charts.Count);
        Assert.Single(charts, part => part.ChartSpace.Descendants<C.DoughnutChart>().Any());
        Assert.Single(charts, part => part.ChartSpace.Descendants<C.BarGrouping>().Any(grouping => grouping.Val! == C.BarGroupingValues.PercentStacked));

        // Every series reads the Charts sheet's own data block.
        Assert.All(charts.SelectMany(part => part.ChartSpace.Descendants<C.Formula>()),
            formula => Assert.StartsWith("'Charts'!", formula.Text));
    }

    [Fact]
    public void The_summary_lists_the_same_top_customers_as_the_page()
    {
        using var workbook = Open(_export.ExportRouteSalesDetailToExcel(Summary(), Mix(), Lines(), Labels()));
        var sheet = workbook.Worksheet("Summary");

        var usdTitle = sheet.CellsUsed().First(cell => cell.GetString() == "Top 5 customers — USD");
        var nameColumn = sheet.Row(usdTitle.Address.RowNumber + 1).CellsUsed().First(cell => cell.GetString() == "Customer").Address.ColumnNumber;
        var first = usdTitle.Address.RowNumber + 2;
        Assert.Equal(["Bravo", "Alpha", "Echo"], Enumerable.Range(first, 3).Select(row => sheet.Cell(row, nameColumn).GetString()));

        Assert.Contains(sheet.CellsUsed(), cell => cell.GetString() == "Top 5 customers — ZWG");
    }

    [Fact]
    public void The_charts_open_on_the_currency_the_page_was_ranking_in()
    {
        using var workbook = Open(_export.ExportRouteSalesDetailToExcel(Summary(), Mix(), Lines(), Labels(), currency: "ZWG"));
        var sheet = workbook.Worksheet("Charts");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Value (ZWG)" && cell.CellLeft().GetString() == "Customer");
        Assert.Equal("Foxtrot", sheet.Cell(header.Address.RowNumber + 1, header.Address.ColumnNumber - 1).GetString());
    }

    [Fact]
    public void Each_currency_has_its_own_value_column_on_the_item_sheet()
    {
        using var workbook = Open(_export.ExportRouteSalesDetailToExcel(Summary(), Mix(), Lines(), Labels()));
        var sheet = workbook.Worksheet("Customer products");

        var usd = HeaderCell(sheet, "Line value (USD)").Address.ColumnNumber;
        var zwg = HeaderCell(sheet, "Line value (ZWG)").Address.ColumnNumber;
        var customer = HeaderCell(sheet, "Customer").Address.ColumnNumber;

        var foxtrot = sheet.Column(customer).CellsUsed().Single(cell => cell.GetString() == "Foxtrot").Address.RowNumber;
        Assert.True(sheet.Cell(foxtrot, usd).IsEmpty());
        Assert.Equal(20_000m, sheet.Cell(foxtrot, zwg).GetValue<decimal>());

        // A line whose customer has gone is kept, under its code.
        Assert.Contains(sheet.Column(customer).CellsUsed(), cell => cell.GetString() == "(customer deleted)");
    }

    [Fact]
    public void The_dormant_sheet_matches_the_visit_list()
    {
        using var workbook = Open(_export.ExportRouteSalesDetailToExcel(Summary(), Mix(), Lines(), Labels()));
        var sheet = workbook.Worksheet("Dormant");

        var header = HeaderCell(sheet, "Customer");
        Assert.Equal(["Delta", "Charlie"], Enumerable.Range(header.Address.RowNumber + 1, 2)
            .Select(row => sheet.Cell(row, header.Address.ColumnNumber).GetString()));
    }

    [Fact]
    public void A_report_with_no_customers_is_still_a_valid_file_without_charts()
    {
        var empty = new RouteCustomerSalesSummaryModel { From = From, To = To, DormantDays = 30 };
        var bytes = _export.ExportRouteSalesDetailToExcel(empty, null, null, Labels());

        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document));
        Assert.Null(SheetPart(document, "Charts").DrawingsPart);
    }

    [Fact]
    public void The_customer_list_export_still_has_its_one_summary_sheet()
    {
        using var workbook = Open(_export.ExportRouteSalesSummaryToExcel(Summary(), Labels()));

        Assert.Equal(["Summary"], workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.NotNull(HeaderCell(workbook.Worksheet("Summary"), "Last sale (all time)"));
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    private static RouteCustomerSalesSummaryModel Summary()
    {
        var van001 = new List<RouteCustomerSalesRowModel>
        {
            Customer(1, "A1", "Alpha", "VAN001", 7, 2, Totals("USD", 5, 500m, 200m, 435m), Totals("ZWG", 2, 9_000m, null, null)),
            Customer(2, "B1", "Bravo", "VAN001", 6, 1, Totals("USD", 6, 800m, 300m, 696m)),
            Customer(3, "C1", "Charlie", "VAN001", 0, null),
            Customer(4, "D1", "Delta", "VAN001", 0, 45)
        };
        var van002 = new List<RouteCustomerSalesRowModel>
        {
            Customer(5, "E1", "Echo", "VAN002", 3, 3, Totals("USD", 3, 300m, 100m, 261m)),
            Customer(6, "F1", "Foxtrot", "VAN002", 4, 5, Totals("ZWG", 4, 20_000m, null, null))
        };

        return new RouteCustomerSalesSummaryModel
        {
            From = From,
            To = To,
            DormantDays = 30,
            Margin = new ManagementMarginStatus { Available = true },
            Routes =
            [
                new RouteSalesGroupModel
                {
                    AssignedBusinessPartnerCode = "VAN001", CustomerCount = 4, SaleCount = 13, NeverBoughtCount = 1, DormantCount = 1,
                    TotalsByCurrency = [Totals("USD", 11, 1_300m, 500m, 1_131m), Totals("ZWG", 2, 9_000m, null, null)],
                    Customers = van001
                },
                new RouteSalesGroupModel
                {
                    AssignedBusinessPartnerCode = "VAN002", CustomerCount = 2, SaleCount = 7,
                    TotalsByCurrency = [Totals("USD", 3, 300m, 100m, 261m), Totals("ZWG", 4, 20_000m, null, null)],
                    Customers = van002
                }
            ]
        };
    }

    private static RouteCustomerProductMixModel Mix() => new()
    {
        From = From,
        To = To,
        Margin = new ManagementMarginStatus { Available = true },
        Items =
        [
            Item("CHE", "Cheddar 500g", 10, 3, Totals("USD", 10, 600m, 200m, 522m)),
            Item("YOG", "Yoghurt 1L", 12, 2, Totals("USD", 12, 400m, 150m, 348m)),
            Item("MLK", "Milk 2L", 12, 4, Totals("USD", 8, 600m, 250m, 522m), Totals("ZWG", 4, 29_000m, null, null))
        ]
    };

    private static RouteCustomerProductSalesModel Lines() => new()
    {
        From = From,
        To = To,
        Margin = new ManagementMarginStatus { Available = true },
        Rows =
        [
            Line(2, "B1", "VAN001", "CHE", "USD", 600m),
            Line(1, "A1", "VAN001", "MLK", "ZWG", 9_000m),
            Line(1, "A1", "VAN001", "YOG", "USD", 500m),
            Line(6, "F1", "VAN002", "MLK", "ZWG", 20_000m),
            Line(5, "E1", "VAN002", "YOG", "USD", 300m),
            Line(null, "Z9", "VAN002", "CHE", "USD", 12m)
        ]
    };

    private static Dictionary<string, string> Labels() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["VAN001"] = "VAN001 - Van Sales East 1",
        ["VAN002"] = "VAN002 - Van Sales East 2"
    };

    private static RouteCustomerSalesRowModel Customer(
        int id, string code, string name, string route, int sales, int? daysSince, params RouteCustomerSalesTotalsModel[] totals) => new()
    {
        RouteCustomerId = id,
        Code = code,
        Name = name,
        AssignedBusinessPartnerCode = route,
        IsActive = true,
        Phone = "077 000 000" + id,
        CapturedAt = From.AddDays(-100),
        SaleCount = sales,
        LineCount = sales * 2,
        FirstSaleAt = daysSince is null ? null : From.AddDays(-60),
        LastSaleAt = daysSince is { } days ? To.AddDays(-days) : null,
        DaysSinceLastSale = daysSince,
        TotalsByCurrency = [.. totals]
    };

    private static RouteCustomerSalesTotalsModel Totals(string currency, int sales, decimal gross, decimal? gp, decimal? net) => new()
    {
        Currency = currency,
        SaleCount = sales,
        Gross = gross,
        GrossProfit = gp,
        CostedNetAmount = net,
        CostedSaleCount = gp is null ? 0 : sales,
        MarginPercent = gp is { } profit && net is { } baseValue ? Math.Round(profit / baseValue * 100m, 2) : null
    };

    private static RouteCustomerProductMixRowModel Item(
        string code, string description, int times, int customers, params RouteCustomerSalesTotalsModel[] totals) => new()
    {
        ItemCode = code,
        ItemDescription = description,
        Quantity = times * 3,
        UoMCode = "EA",
        LineCount = times,
        CustomerCount = customers,
        LastSoldAt = To.AddDays(-1),
        TotalsByCurrency = [.. totals]
    };

    private static RouteCustomerProductSalesRowModel Line(int? customerId, string customerCode, string route, string item, string currency, decimal value) => new()
    {
        AssignedBusinessPartnerCode = route,
        RouteCustomerId = customerId,
        RouteCustomerCode = customerCode,
        ItemCode = item,
        ItemDescription = item + " item",
        UoMCode = "EA",
        Currency = currency,
        Quantity = 2,
        LineTotal = value,
        LineCount = 1,
        LastSoldAt = To.AddDays(-2)
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
