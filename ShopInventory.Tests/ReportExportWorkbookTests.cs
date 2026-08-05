using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The export service has no seam to a database — it takes a report object and returns
/// workbook bytes — so these open what it produced and read the cells back.
/// </summary>
/// <remarks>
/// Two things are worth guarding. An empty report is the case that reaches production
/// first and the one that used to render as a bare header over a totals row of zeros.
/// And the difference between a date and its rendering, or a number and its rendering,
/// is invisible in a screenshot but decides whether the column can be sorted or summed.
/// </remarks>
public class ReportExportWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    public static TheoryData<string, Func<ReportExportService, byte[]>> EmptyReports() => new()
    {
        { "Sales Summary", s => s.ExportSalesSummaryToExcel(new SalesSummaryReport { FromDate = From, ToDate = To }) },
        { "Top Products", s => s.ExportTopProductsToExcel(new TopProductsReport { FromDate = From, ToDate = To }) },
        { "Stock Summary", s => s.ExportStockSummaryToExcel(new StockSummaryReport { ReportDate = To }) },
        { "Payment Summary", s => s.ExportPaymentSummaryToExcel(new PaymentSummaryReport { FromDate = From, ToDate = To }) },
        { "Top Customers", s => s.ExportTopCustomersToExcel(new TopCustomersReport { FromDate = From, ToDate = To }) },
        { "Low Stock", s => s.ExportLowStockAlertsToExcel(new LowStockAlertReport { ReportDate = To }) },
        { "Order Fulfillment", s => s.ExportOrderFulfillmentToExcel(new OrderFulfillmentReport { FromDate = From, ToDate = To }) },
        { "Credit Notes", s => s.ExportCreditNoteSummaryToExcel(new CreditNoteSummaryReport { FromDate = From, ToDate = To }) },
        { "Purchase Orders", s => s.ExportPurchaseOrderSummaryToExcel(new PurchaseOrderSummaryReport { FromDate = From, ToDate = To }) },
        { "Receivables Aging", s => s.ExportReceivablesAgingToExcel(new ReceivablesAgingReport { ReportDate = To }) },
        { "Profit Overview", s => s.ExportProfitOverviewToExcel(new ProfitOverviewReport { FromDate = From, ToDate = To }) },
        { "Slow Moving", s => s.ExportSlowMovingProductsToExcel(new SlowMovingProductsReport { FromDate = From, ToDate = To }) },
        { "Desktop Sales", s => s.ExportDesktopSalesToExcel([], null, From, To) },
        { "Local Stock", s => s.ExportLocalStockToExcel(new LocalStockResultDto { WarehouseCode = "01", SnapshotDate = To }) },
        { "Mobile Orders", s => s.ExportMobileOrdersToExcel([], "Mobile Orders") },
    };

    [Theory]
    [MemberData(nameof(EmptyReports))]
    public void An_empty_report_still_produces_a_readable_workbook(string name, Func<ReportExportService, byte[]> export)
    {
        var bytes = export(_service);

        using var workbook = Open(bytes);

        Assert.NotEmpty(workbook.Worksheets);

        foreach (var sheet in workbook.Worksheets)
        {
            var text = string.Join("\n", sheet.CellsUsed().Select(c => c.GetFormattedString()));

            // The letterhead identifies the report even when there is nothing under it.
            Assert.Contains("KEFALOS CHEESE (PVT) LTD", text);
            Assert.Contains("CONFIDENTIAL", text);
            Assert.False(sheet.ShowGridLines, $"{name}/{sheet.Name} left the worksheet grid showing");
        }
    }

    [Fact]
    public void An_empty_table_says_so_rather_than_showing_a_bare_header()
    {
        var bytes = _service.ExportTopProductsToExcel(new TopProductsReport { FromDate = From, ToDate = To });

        using var workbook = Open(bytes);
        var sheet = workbook.Worksheet("Top Products");

        var text = string.Join("\n", sheet.CellsUsed().Select(c => c.GetFormattedString()));
        Assert.Contains("No products were sold in this period.", text);
    }

    [Fact]
    public void Dates_are_written_as_dates_not_as_their_rendering()
    {
        var report = new SalesSummaryReport
        {
            FromDate = From,
            ToDate = To,
            TotalInvoices = 2,
            DailySales =
            [
                new DailySales { Date = new DateTime(2026, 7, 2), InvoiceCount = 1, TotalSalesUSD = 100m },
                new DailySales { Date = new DateTime(2026, 7, 1), InvoiceCount = 1, TotalSalesUSD = 250m },
            ],
        };

        using var workbook = Open(_service.ExportSalesSummaryToExcel(report));
        var sheet = workbook.Worksheet("Daily Sales");

        var headerRow = sheet.CellsUsed().First(c => c.GetString() == "Date").Address.RowNumber;
        var firstDataCell = sheet.Cell(headerRow + 1, 1);

        Assert.Equal(XLDataType.DateTime, firstDataCell.DataType);
        Assert.Equal(new DateTime(2026, 7, 2), firstDataCell.GetDateTime());
    }

    [Fact]
    public void Money_columns_name_their_own_currency_and_redden_negatives()
    {
        var report = new SalesSummaryReport
        {
            FromDate = From,
            ToDate = To,
            DailySales = [new DailySales { Date = From, InvoiceCount = 1, TotalSalesUSD = 10m, TotalSalesZIG = 20m }],
        };

        using var workbook = Open(_service.ExportSalesSummaryToExcel(report));
        var sheet = workbook.Worksheet("Daily Sales");

        var headerRow = sheet.CellsUsed().First(c => c.GetString() == "Date").Address.RowNumber;

        Assert.Contains("$", sheet.Cell(headerRow + 1, 3).Style.NumberFormat.Format);
        Assert.Contains("ZiG", sheet.Cell(headerRow + 1, 4).Style.NumberFormat.Format);
        Assert.Contains("[Red]", sheet.Cell(headerRow + 1, 3).Style.NumberFormat.Format);
    }

    [Fact]
    public void A_populated_table_carries_filter_dropdowns_and_a_total_that_follows_them()
    {
        var report = new TopProductsReport
        {
            FromDate = From,
            ToDate = To,
            TotalProductsSold = 3,
            TopProducts =
            [
                new TopProduct { Rank = 1, ItemCode = "CHE001", ItemName = "Feta 200g", TotalQuantitySold = 10, TimesOrdered = 3, TotalRevenueUSD = 90m },
                new TopProduct { Rank = 2, ItemCode = "CHE002", ItemName = "Halloumi 250g", TotalQuantitySold = 4, TimesOrdered = 2, TotalRevenueUSD = 48m },
            ],
        };

        using var workbook = Open(_service.ExportTopProductsToExcel(report));
        var sheet = workbook.Worksheet("Top Products");

        Assert.True(sheet.AutoFilter.IsEnabled, "the register has no filter dropdowns");

        var totalCell = sheet.CellsUsed().First(c => c.GetString() == "TOTAL");
        var revenueTotal = sheet.Cell(totalCell.Address.RowNumber, 6);

        // SUBTOTAL, not SUM: filtering the table has to move the total with it.
        Assert.Contains("SUBTOTAL(109", revenueTotal.FormulaA1);
    }

    [Fact]
    public void Every_sheet_is_set_up_to_print()
    {
        var report = new TopCustomersReport
        {
            FromDate = From,
            ToDate = To,
            TopCustomers = [new TopCustomer { Rank = 1, CardCode = "ABS006", CardName = "A Customer", InvoiceCount = 2, TotalPurchasesUSD = 400m }],
        };

        using var workbook = Open(_service.ExportTopCustomersToExcel(report));
        var sheet = workbook.Worksheet("Top Customers");

        Assert.Equal(XLPaperSize.A4Paper, sheet.PageSetup.PaperSize);
        Assert.True(sheet.PageSetup.FirstRowToRepeatAtTop > 0, "page 2 would print without column headings");
        // Read back as odd pages: a header saved for all pages round-trips through the
        // odd/even/first slots the file format actually has.
        Assert.Contains("KEFALOS", sheet.PageSetup.Header.Left.GetText(XLHFOccurrence.OddPages));
        Assert.Contains("Page", sheet.PageSetup.Footer.Right.GetText(XLHFOccurrence.OddPages));
    }

    [Fact]
    public void The_workbook_carries_its_own_identity_in_the_file_properties()
    {
        using var workbook = Open(_service.ExportStockSummaryToExcel(new StockSummaryReport { ReportDate = To }));

        Assert.Equal("Stock Summary Report", workbook.Properties.Title);
        Assert.Equal("KEFALOS CHEESE (PVT) LTD", workbook.Properties.Company);
    }

    [Fact]
    public void Row_striping_does_not_paint_over_a_status_badge()
    {
        var report = new LowStockAlertReport
        {
            ReportDate = To,
            TotalAlerts = 2,
            CriticalCount = 1,
            WarningCount = 1,
            Items =
            [
                new ShopInventory.Web.Models.LowStockItem { ItemCode = "CHE001", ItemName = "Feta 200g", WarehouseCode = "01", AlertLevel = "Critical", CurrentStock = 0, ReorderLevel = 50, SuggestedReorderQty = 100 },
                new ShopInventory.Web.Models.LowStockItem { ItemCode = "CHE002", ItemName = "Halloumi 250g", WarehouseCode = "01", AlertLevel = "Warning", CurrentStock = 12, ReorderLevel = 40, SuggestedReorderQty = 60 },
            ],
        };

        using var workbook = Open(_service.ExportLowStockAlertsToExcel(report));
        var sheet = workbook.Worksheet("Low Stock Alerts");

        var critical = sheet.CellsUsed().First(c => c.GetString() == "CRITICAL");
        var warning = sheet.CellsUsed().First(c => c.GetString() == "WARNING");

        // Both badges keep their own fill; the stripe must not be the thing that shows.
        Assert.Equal(XLColor.FromHtml("#c62828"), critical.Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.FromHtml("#fff3cd"), warning.Style.Fill.BackgroundColor);
    }

    [Fact]
    public void A_mixed_currency_register_totals_each_currency_separately()
    {
        var sales = new List<DesktopSaleDto>
        {
            new() { ExternalReferenceId = "R1", CardCode = "C1", DocDate = From, Currency = "USD", TotalAmount = 100m },
            new() { ExternalReferenceId = "R2", CardCode = "C2", DocDate = From, Currency = "ZWG", TotalAmount = 5000m },
        };

        using var workbook = Open(_service.ExportDesktopSalesToExcel(sales, null, From, To));
        var sheet = workbook.Worksheet("Desktop Sales");

        var totals = sheet.CellsUsed()
            .Where(c => c.GetString().StartsWith("Total (", StringComparison.Ordinal))
            .ToList();

        // One per currency, never a single sum across both.
        Assert.Equal(2, totals.Count);
    }
}
