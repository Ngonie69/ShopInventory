using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ShopInventory.Web.Common;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The two workbooks a shop stock user exports: their partner's sales (the analysis plus every sale) from
/// /shop-sales, and a warehouse's stock from /stock-in-hand. Each is opened and its cells read back.
/// </summary>
public class ShopStockExportWorkbookTests
{
    private readonly ReportExportService _export = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static void AssertValid(byte[] bytes)
    {
        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(error => $"{error.Path?.XPath}: {error.Description}")));
    }

    // ── Sales ────────────────────────────────────────────────────────────────────────────────

    private static DesktopSalesAnalysisResult PartnerReport() => new()
    {
        FromDate = new DateTime(2026, 11, 16),
        ToDate = new DateTime(2026, 11, 21),
        GeneratedAtUtc = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc),
        CardCode = "CIS006",
        CreditsCounted = true,
        PaymentMethods = ["Cash"],
        Currencies =
        [
            new DesktopSalesCurrencyAnalysis
            {
                Currency = "USD",
                SalesCount = 2,
                TotalAmount = 150m,
                ByBusinessPartner = [new() { Key = "CIS006", Label = "Counter Sales", SalesCount = 2, TotalAmount = 150m }],
            },
        ],
    };

    private static List<DesktopSaleDto> PartnerSales() =>
    [
        new() { ExternalReferenceId = "S-2", CardCode = "CIS006", CardName = "Counter Sales", WarehouseCode = "KEFSHOP", DocDate = new DateTime(2026, 11, 20), Currency = "USD", TotalAmount = 50m, AmountPaid = 50m, PaymentMethod = "Cash", ConsolidationStatus = "Pending" },
        new() { ExternalReferenceId = "S-1", CardCode = "CIS006", CardName = "Counter Sales", WarehouseCode = "KEFSHOP", DocDate = new DateTime(2026, 11, 17), Currency = "USD", TotalAmount = 100m, AmountPaid = 100m, PaymentMethod = "Cash", ConsolidationStatus = "Consolidated" },
    ];

    [Fact]
    public void The_partner_sales_workbook_ends_with_every_sale_in_date_order_and_a_total()
    {
        var bytes = _export.ExportDesktopSalesAnalysisToExcel(PartnerReport(), PartnerSales());
        AssertValid(bytes);

        using var workbook = Open(bytes);
        Assert.Equal("Sales", workbook.Worksheets.Last().Name);

        var sheet = workbook.Worksheet("Sales");
        Assert.Contains(sheet.CellsUsed(), cell => cell.GetString().Contains("Business partner: Counter Sales"));

        int Column(string header) => sheet.CellsUsed().Single(c => c.GetString() == header).Address.ColumnNumber;
        var first = sheet.CellsUsed().Single(c => c.GetString() == "S-1").WorksheetRow();
        var second = sheet.CellsUsed().Single(c => c.GetString() == "S-2").WorksheetRow();
        Assert.True(first.RowNumber() < second.RowNumber());
        Assert.Equal(new DateTime(2026, 11, 17), first.Cell(1).GetDateTime());
        Assert.Equal(100m, first.Cell(Column("Amount")).GetValue<decimal>());
        Assert.Equal("Consolidated", first.Cell(Column("Consolidation")).GetString());

        var totals = sheet.CellsUsed().Single(c => c.GetString().StartsWith("Total (", StringComparison.Ordinal)).WorksheetRow();
        Assert.Equal(150m, totals.Cell(Column("Amount")).GetValue<decimal>());
    }

    [Fact]
    public void The_analysis_alone_has_no_sales_sheet_and_no_sales_says_so()
    {
        using (var workbook = Open(_export.ExportDesktopSalesAnalysisToExcel(PartnerReport())))
        {
            Assert.DoesNotContain(workbook.Worksheets, sheet => sheet.Name == "Sales");
        }

        using var empty = Open(_export.ExportDesktopSalesAnalysisToExcel(PartnerReport(), []));
        Assert.Contains(empty.Worksheet("Sales").CellsUsed(), cell => cell.GetString() == "No shop or vending sales fell in this period.");
    }

    [Fact]
    public void A_stock_account_sees_no_gross_profit_anywhere()
    {
        using var workbook = Open(_export.ExportDesktopSalesAnalysisToExcel(PartnerReport(), PartnerSales()));

        Assert.All(workbook.Worksheets, sheet =>
            Assert.DoesNotContain(sheet.CellsUsed(), cell => cell.GetString().Contains("Gross Profit", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Stock ────────────────────────────────────────────────────────────────────────────────

    private static List<StockInHandRow> LiveRows() =>
    [
        new("A100", "Cheddar 1kg", "KG", 12m, 12m, 2m),
        new("A200", "Feta 500g", "EA", 0m, 0m, 0m),
        new("A300", "Gouda 2kg", "KG", 3m, 3m, 5m),
    ];

    [Fact]
    public void Stock_now_lists_every_item_with_committed_free_and_a_status()
    {
        var bytes = _export.ExportStockInHandToExcel(new StockInHandExport("KEFSHOP", "Factory Shop", null, LiveRows()));
        AssertValid(bytes);

        using var workbook = Open(bytes);
        var sheet = workbook.Worksheet("Stock in Hand");
        Assert.Contains(sheet.CellsUsed(), cell => cell.GetString() == "Stock in Hand · Factory Shop (KEFSHOP)");

        int Column(string header) => sheet.CellsUsed().Single(c => c.GetString() == header).Address.ColumnNumber;
        var cheddar = sheet.CellsUsed().Single(c => c.GetString() == "A100").WorksheetRow();
        var feta = sheet.CellsUsed().Single(c => c.GetString() == "A200").WorksheetRow();
        var gouda = sheet.CellsUsed().Single(c => c.GetString() == "A300").WorksheetRow();

        Assert.Equal((12m, 2m, 10m), (
            cheddar.Cell(Column("On Hand")).GetValue<decimal>(),
            cheddar.Cell(Column("Committed")).GetValue<decimal>(),
            cheddar.Cell(Column("Free to Sell")).GetValue<decimal>()));
        Assert.Equal("On the shelf", cheddar.Cell(Column("Status")).GetString());
        Assert.Equal("At zero", feta.Cell(Column("Status")).GetString());
        Assert.Equal(-2m, gouda.Cell(Column("Free to Sell")).GetValue<decimal>());
        Assert.Equal("Oversold", gouda.Cell(Column("Status")).GetString());

        // Each item counts in its own unit, so nothing is summed down a quantity column.
        Assert.DoesNotContain(sheet.CellsUsed(), cell => cell.GetString().StartsWith("Total", StringComparison.Ordinal));
    }

    [Fact]
    public void A_past_moment_shows_then_against_now_and_names_the_moment()
    {
        List<StockInHandRow> rows =
        [
            new("A100", "Cheddar 1kg", "KG", 4m, 12m, 0m),
            new("A200", "Feta 500g", "EA", 6m, 1m, 0m),
        ];
        var atUtc = new DateTime(2026, 11, 20, 15, 30, 0, DateTimeKind.Utc); // 17:30 CAT
        var bytes = _export.ExportStockInHandToExcel(new StockInHandExport("KEFSHOP", "Factory Shop", atUtc, rows));
        AssertValid(bytes);

        using var workbook = Open(bytes);
        var sheet = workbook.Worksheet("Stock in Hand");
        Assert.Contains(sheet.CellsUsed(), cell => cell.GetString().Contains("As at Fri 20 Nov 2026, 17:30 CAT"));

        int Column(string header) => sheet.CellsUsed().Single(c => c.GetString() == header).Address.ColumnNumber;
        var cheddar = sheet.CellsUsed().Single(c => c.GetString() == "A100").WorksheetRow();
        var feta = sheet.CellsUsed().Single(c => c.GetString() == "A200").WorksheetRow();

        Assert.Equal((4m, 12m, 8m), (
            cheddar.Cell(Column("On Hand Then")).GetValue<decimal>(),
            cheddar.Cell(Column("On Hand Now")).GetValue<decimal>(),
            cheddar.Cell(Column("Change")).GetValue<decimal>()));
        Assert.Equal("Up since", cheddar.Cell(Column("Status")).GetString());
        Assert.Equal("Down since", feta.Cell(Column("Status")).GetString());
        Assert.DoesNotContain(sheet.CellsUsed(), cell => cell.GetString() == "Committed");
    }

    [Fact]
    public void An_empty_warehouse_still_produces_a_readable_workbook()
    {
        var bytes = _export.ExportStockInHandToExcel(new StockInHandExport("KEFSHOP", "", null, []));
        AssertValid(bytes);

        using var workbook = Open(bytes);
        Assert.Contains(workbook.Worksheet("Stock in Hand").CellsUsed(), cell => cell.GetString() == "This warehouse holds no items.");
    }
}
