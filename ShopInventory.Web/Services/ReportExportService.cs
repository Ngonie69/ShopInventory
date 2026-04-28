using ClosedXML.Excel;
using ShopInventory.Web.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;
using ShopInventory.Web.Models;
using System.Text;

namespace ShopInventory.Web.Services;

/// <summary>
/// Service for exporting reports to Excel and PDF-ready HTML
/// </summary>
public interface IReportExportService
{
    byte[] ExportSalesSummaryToExcel(SalesSummaryReport report);
    byte[] ExportTopProductsToExcel(TopProductsReport report);
    byte[] ExportStockSummaryToExcel(StockSummaryReport report);
    byte[] ExportPaymentSummaryToExcel(PaymentSummaryReport report);
    byte[] ExportTopCustomersToExcel(TopCustomersReport report);
    byte[] ExportLowStockAlertsToExcel(LowStockAlertReport report);
    byte[] ExportOrderFulfillmentToExcel(OrderFulfillmentReport report);
    byte[] ExportCreditNoteSummaryToExcel(CreditNoteSummaryReport report);
    byte[] ExportPurchaseOrderSummaryToExcel(PurchaseOrderSummaryReport report);
    byte[] ExportReceivablesAgingToExcel(ReceivablesAgingReport report);
    byte[] ExportProfitOverviewToExcel(ProfitOverviewReport report);
    byte[] ExportSlowMovingProductsToExcel(SlowMovingProductsReport report);
    byte[] ExportPodUploadStatusToExcel(PodUploadStatusReport report);
    byte[] ExportTimesheetReportToExcel(TimesheetReportResponse report, DateTime? fromDate = null, DateTime? toDate = null);
    byte[] ExportDesktopSalesToExcel(List<DesktopSaleDto> sales, EndOfDayReportDto? report, DateTime? fromDate = null, DateTime? toDate = null);
    byte[] ExportLocalStockToExcel(LocalStockResultDto stock);
    byte[] ExportMerchandiserPurchaseOrderReportToExcel(GetMerchandiserPurchaseOrderReportResult report);
    string GeneratePrintableHtml(string title, string content, DateTime? fromDate = null, DateTime? toDate = null);
}

public class ReportExportService : IReportExportService
{
    private const string CompanyName = "KEFALOS CHEESE (PVT) LTD";
    private const string SystemName = "Shop Inventory Management System";
    private static readonly XLColor NavyBlue = XLColor.FromHtml("#1a237e");
    private static readonly XLColor LightNavy = XLColor.FromHtml("#283593");
    private static readonly XLColor AccentBlue = XLColor.FromHtml("#e8eaf6");
    private static readonly XLColor LightGray = XLColor.FromHtml("#f5f5f5");
    private static readonly XLColor MedGray = XLColor.FromHtml("#e0e0e0");
    private static readonly XLColor BorderGray = XLColor.FromHtml("#bdbdbd");
    private static readonly XLColor KpiBackground = XLColor.FromHtml("#f0f4ff");
    private static readonly XLColor TotalsBackground = XLColor.FromHtml("#e8eaf6");
    private static readonly XLColor SuccessGreen = XLColor.FromHtml("#2e7d32");
    private static readonly XLColor DangerRed = XLColor.FromHtml("#c62828");
    private static readonly XLColor WarningOrange = XLColor.FromHtml("#e65100");

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime CurrentCatNow() => IAuditService.ToCAT(DateTime.UtcNow);

    private static string FormatCatDateTime(DateTime utcDateTime) =>
        IAuditService.ToCAT(EnsureUtc(utcDateTime)).ToString("dd MMM yyyy HH:mm");

    private static string FormatCatDate(DateTime utcDateTime) =>
        IAuditService.ToCAT(EnsureUtc(utcDateTime)).ToString("dd MMM yyyy");

    /// <summary>
    /// Creates the professional report header on a worksheet and returns the next available row.
    /// </summary>
    private static int WriteReportHeader(IXLWorksheet ws, string reportTitle, int colSpan, DateTime? fromDate = null, DateTime? toDate = null, string? subtitle = null)
    {
        var generatedAt = CurrentCatNow();

        // Company name
        ws.Range(1, 1, 1, colSpan).Merge();
        ws.Cell(1, 1).Value = CompanyName;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 18;
        ws.Cell(1, 1).Style.Font.FontColor = NavyBlue;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        // System subtitle
        ws.Range(2, 1, 2, colSpan).Merge();
        ws.Cell(2, 1).Value = SystemName;
        ws.Cell(2, 1).Style.Font.FontSize = 10;
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#757575");
        ws.Cell(2, 1).Style.Font.Italic = true;

        // Thin navy line under header
        ws.Range(2, 1, 2, colSpan).Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        ws.Range(2, 1, 2, colSpan).Style.Border.BottomBorderColor = NavyBlue;

        // Report title
        ws.Range(4, 1, 4, colSpan).Merge();
        ws.Cell(4, 1).Value = reportTitle;
        ws.Cell(4, 1).Style.Font.Bold = true;
        ws.Cell(4, 1).Style.Font.FontSize = 14;
        ws.Cell(4, 1).Style.Font.FontColor = LightNavy;

        // Period / date line
        string dateLine;
        if (fromDate.HasValue && toDate.HasValue)
            dateLine = $"Period: {fromDate:dd MMM yyyy} \u2013 {toDate:dd MMM yyyy}     |     Generated: {generatedAt:dd MMM yyyy HH:mm} CAT";
        else if (subtitle != null)
            dateLine = $"{subtitle}     |     Generated: {generatedAt:dd MMM yyyy HH:mm} CAT";
        else
            dateLine = $"Generated: {generatedAt:dd MMM yyyy HH:mm} CAT";

        ws.Range(5, 1, 5, colSpan).Merge();
        ws.Cell(5, 1).Value = dateLine;
        ws.Cell(5, 1).Style.Font.FontSize = 10;
        ws.Cell(5, 1).Style.Font.FontColor = XLColor.FromHtml("#616161");
        ws.Cell(5, 1).Style.Font.Italic = true;

        return 7; // next available row
    }

    /// <summary>
    /// Writes a KPI metric card (2 rows: value on top, label below).
    /// </summary>
    private static void WriteKpiCard(IXLWorksheet ws, int row, int col, string label, string value, XLColor? valueColor = null)
    {
        ws.Cell(row, col).Value = value;
        ws.Cell(row, col).Style.Font.Bold = true;
        ws.Cell(row, col).Style.Font.FontSize = 16;
        ws.Cell(row, col).Style.Font.FontColor = valueColor ?? NavyBlue;
        ws.Cell(row, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, col).Style.Fill.BackgroundColor = KpiBackground;
        ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row, col).Style.Border.OutsideBorderColor = MedGray;

        ws.Cell(row + 1, col).Value = label;
        ws.Cell(row + 1, col).Style.Font.FontSize = 9;
        ws.Cell(row + 1, col).Style.Font.FontColor = XLColor.FromHtml("#616161");
        ws.Cell(row + 1, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row + 1, col).Style.Fill.BackgroundColor = KpiBackground;
        ws.Cell(row + 1, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row + 1, col).Style.Border.OutsideBorderColor = MedGray;
    }

    /// <summary>
    /// Writes a 2-column KPI metric row (label: value) for summary sections.
    /// </summary>
    private static void WriteKpiRow(IXLWorksheet ws, int row, string label, string value, bool highlight = false)
    {
        ws.Cell(row, 1).Value = label;
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 10;
        ws.Cell(row, 1).Style.Fill.BackgroundColor = KpiBackground;
        ws.Cell(row, 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row, 1).Style.Border.OutsideBorderColor = MedGray;
        ws.Cell(row, 1).Style.Alignment.Indent = 1;

        ws.Cell(row, 2).Value = value;
        ws.Cell(row, 2).Style.Font.FontSize = 11;
        ws.Cell(row, 2).Style.Font.Bold = highlight;
        ws.Cell(row, 2).Style.Fill.BackgroundColor = KpiBackground;
        ws.Cell(row, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row, 2).Style.Border.OutsideBorderColor = MedGray;
        ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        if (highlight) ws.Cell(row, 2).Style.Font.FontColor = NavyBlue;
    }

    /// <summary>
    /// Styles the table header row with navy background and white text.
    /// </summary>
    private static void StyleTableHeader(IXLWorksheet ws, int headerRow, int lastCol)
    {
        var headerRange = ws.Range(headerRow, 1, headerRow, lastCol);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontSize = 10;
        headerRange.Style.Fill.BackgroundColor = NavyBlue;
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        headerRange.Style.Border.BottomBorderColor = XLColor.FromHtml("#0d47a1");
        ws.Row(headerRow).Height = 22;
    }

    /// <summary>
    /// Styles data rows with alternating colors and borders.
    /// </summary>
    private static void StyleDataRows(IXLWorksheet ws, int firstDataRow, int lastRow, int lastCol)
    {
        if (lastRow < firstDataRow) return;
        var dataRange = ws.Range(firstDataRow, 1, lastRow, lastCol);
        dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.InsideBorderColor = MedGray;
        dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.OutsideBorderColor = BorderGray;
        dataRange.Style.Font.FontSize = 10;

        for (int r = firstDataRow; r <= lastRow; r++)
        {
            if ((r - firstDataRow) % 2 == 1)
                ws.Range(r, 1, r, lastCol).Style.Fill.BackgroundColor = LightGray;
        }
    }

    /// <summary>
    /// Styles a totals row with distinct background and bold font.
    /// </summary>
    private static void StyleTotalsRow(IXLWorksheet ws, int row, int lastCol)
    {
        var totalsRange = ws.Range(row, 1, row, lastCol);
        totalsRange.Style.Font.Bold = true;
        totalsRange.Style.Font.FontSize = 10;
        totalsRange.Style.Fill.BackgroundColor = TotalsBackground;
        totalsRange.Style.Font.FontColor = NavyBlue;
        totalsRange.Style.Border.TopBorder = XLBorderStyleValues.Double;
        totalsRange.Style.Border.TopBorderColor = NavyBlue;
        totalsRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        totalsRange.Style.Border.BottomBorderColor = NavyBlue;
    }

    /// <summary>
    /// Writes a confidential footer below the data area.
    /// </summary>
    private static void WriteFooter(IXLWorksheet ws, int row, int colSpan)
    {
        var generatedAt = CurrentCatNow();

        row += 2;
        ws.Range(row, 1, row, colSpan).Merge();
        ws.Range(row, 1, row, colSpan).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        ws.Range(row, 1, row, colSpan).Style.Border.TopBorderColor = BorderGray;
        ws.Cell(row, 1).Value = $"CONFIDENTIAL  \u2022  {CompanyName}  \u2022  {SystemName}  \u2022  Generated {generatedAt:dd MMM yyyy HH:mm} CAT";
        ws.Cell(row, 1).Style.Font.FontSize = 8;
        ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#9e9e9e");
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    /// <summary>
    /// Final adjustments: auto-fit columns, freeze header, page setup.
    /// </summary>
    private static void FinalizeSheet(IXLWorksheet ws, int lastCol, int freezeRow = 0, bool landscape = false)
    {
        ws.Columns(1, lastCol).AdjustToContents();
        for (int c = 1; c <= lastCol; c++)
        {
            if (ws.Column(c).Width > 40) ws.Column(c).Width = 40;
            if (ws.Column(c).Width < 10) ws.Column(c).Width = 10;
        }
        if (freezeRow > 0) ws.SheetView.FreezeRows(freezeRow);
        ws.PageSetup.PageOrientation = landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetLeft(0.5);
        ws.PageSetup.Margins.SetRight(0.5);
        ws.PageSetup.Margins.SetTop(0.5);
        ws.PageSetup.Margins.SetBottom(0.5);
    }

    // ═══════════════════════════════════════════════════════════════
    // SALES SUMMARY
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportSalesSummaryToExcel(SalesSummaryReport report)
    {
        using var workbook = new XLWorkbook();

        // ── Dashboard Sheet ──
        var dash = workbook.Worksheets.Add("Sales Dashboard");
        int row = WriteReportHeader(dash, "Sales Summary Report", 6, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total Invoices", report.TotalInvoices.ToString("N0"));
        WriteKpiCard(dash, row, 2, "Total Sales (USD)", $"${report.TotalSalesUSD:N2}");
        WriteKpiCard(dash, row, 3, "Total Sales (ZIG)", $"ZIG {report.TotalSalesZIG:N2}");
        WriteKpiCard(dash, row, 4, "VAT (USD)", $"${report.TotalVatUSD:N2}");
        WriteKpiCard(dash, row, 5, "Avg Invoice (USD)", $"${report.AverageInvoiceValueUSD:N2}");
        WriteKpiCard(dash, row, 6, "Unique Customers", report.UniqueCustomers.ToString("N0"));
        row += 3;

        if (report.SalesByCurrency.Any())
        {
            dash.Range(row, 1, row, 6).Merge();
            dash.Cell(row, 1).Value = "SALES BY CURRENCY";
            dash.Cell(row, 1).Style.Font.Bold = true;
            dash.Cell(row, 1).Style.Font.FontSize = 11;
            dash.Cell(row, 1).Style.Font.FontColor = LightNavy;
            row++;

            dash.Cell(row, 1).Value = "Currency"; dash.Cell(row, 2).Value = "Invoices";
            dash.Cell(row, 3).Value = "Total Sales"; dash.Cell(row, 4).Value = "Total VAT";
            StyleTableHeader(dash, row, 4);
            row++;
            int dataStart = row;
            foreach (var curr in report.SalesByCurrency)
            {
                dash.Cell(row, 1).Value = curr.Currency;
                dash.Cell(row, 2).Value = curr.InvoiceCount;
                dash.Cell(row, 3).Value = curr.TotalSales; dash.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
                dash.Cell(row, 4).Value = curr.TotalVat; dash.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            StyleDataRows(dash, dataStart, row - 1, 4);
        }

        WriteFooter(dash, row, 6);
        FinalizeSheet(dash, 6, landscape: true);

        // ── Daily Breakdown Sheet ──
        var daily = workbook.Worksheets.Add("Daily Sales");
        int dRow = WriteReportHeader(daily, "Daily Sales Breakdown", 4, report.FromDate, report.ToDate);

        daily.Cell(dRow, 1).Value = "Date"; daily.Cell(dRow, 2).Value = "Invoices";
        daily.Cell(dRow, 3).Value = "Sales (USD)"; daily.Cell(dRow, 4).Value = "Sales (ZIG)";
        StyleTableHeader(daily, dRow, 4);
        int freezeAt = dRow;
        dRow++;
        int dailyStart = dRow;
        foreach (var day in report.DailySales.OrderByDescending(d => d.Date))
        {
            daily.Cell(dRow, 1).Value = day.Date.ToString("ddd, dd MMM yyyy");
            daily.Cell(dRow, 2).Value = day.InvoiceCount;
            daily.Cell(dRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            daily.Cell(dRow, 3).Value = day.TotalSalesUSD; daily.Cell(dRow, 3).Style.NumberFormat.Format = "$#,##0.00";
            daily.Cell(dRow, 4).Value = day.TotalSalesZIG; daily.Cell(dRow, 4).Style.NumberFormat.Format = "#,##0.00";
            dRow++;
        }
        StyleDataRows(daily, dailyStart, dRow - 1, 4);

        daily.Cell(dRow, 1).Value = "TOTAL";
        daily.Cell(dRow, 2).Value = report.TotalInvoices;
        daily.Cell(dRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        daily.Cell(dRow, 3).Value = report.TotalSalesUSD; daily.Cell(dRow, 3).Style.NumberFormat.Format = "$#,##0.00";
        daily.Cell(dRow, 4).Value = report.TotalSalesZIG; daily.Cell(dRow, 4).Style.NumberFormat.Format = "#,##0.00";
        StyleTotalsRow(daily, dRow, 4);

        WriteFooter(daily, dRow + 1, 4);
        FinalizeSheet(daily, 4, freezeAt);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // TOP PRODUCTS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportTopProductsToExcel(TopProductsReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Top Products");
        int row = WriteReportHeader(ws, "Top Products Report", 7, report.FromDate, report.ToDate);

        WriteKpiCard(ws, row, 1, "Total Products Sold", report.TotalProductsSold.ToString("N0"));
        WriteKpiCard(ws, row, 2, "Products Listed", report.TopProducts.Count.ToString("N0"));
        WriteKpiCard(ws, row, 3, "Total Revenue (USD)", $"${report.TopProducts.Sum(p => p.TotalRevenueUSD):N2}");
        WriteKpiCard(ws, row, 4, "Total Orders", report.TopProducts.Sum(p => p.TimesOrdered).ToString("N0"));
        row += 3;

        ws.Cell(row, 1).Value = "Rank"; ws.Cell(row, 2).Value = "Item Code"; ws.Cell(row, 3).Value = "Product Name";
        ws.Cell(row, 4).Value = "Qty Sold"; ws.Cell(row, 5).Value = "Times Ordered";
        ws.Cell(row, 6).Value = "Revenue (USD)"; ws.Cell(row, 7).Value = "Revenue (ZIG)";
        StyleTableHeader(ws, row, 7);
        int freezeAt = row;
        row++;
        int dataStart = row;
        foreach (var p in report.TopProducts)
        {
            ws.Cell(row, 1).Value = p.Rank;
            ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (p.Rank <= 3)
            {
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontColor = p.Rank == 1 ? XLColor.FromHtml("#ff8f00") : p.Rank == 2 ? XLColor.FromHtml("#757575") : XLColor.FromHtml("#8d6e63");
            }
            ws.Cell(row, 2).Value = p.ItemCode;
            ws.Cell(row, 3).Value = p.ItemName;
            ws.Cell(row, 4).Value = p.TotalQuantitySold; ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 5).Value = p.TimesOrdered; ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 6).Value = p.TotalRevenueUSD; ws.Cell(row, 6).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 7).Value = p.TotalRevenueZIG; ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        StyleDataRows(ws, dataStart, row - 1, 7);

        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 4).Value = report.TopProducts.Sum(p => p.TotalQuantitySold); ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0";
        ws.Cell(row, 5).Value = report.TopProducts.Sum(p => p.TimesOrdered); ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
        ws.Cell(row, 6).Value = report.TopProducts.Sum(p => p.TotalRevenueUSD); ws.Cell(row, 6).Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell(row, 7).Value = report.TopProducts.Sum(p => p.TotalRevenueZIG); ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
        StyleTotalsRow(ws, row, 7);

        WriteFooter(ws, row + 1, 7);
        FinalizeSheet(ws, 7, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // STOCK SUMMARY
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportStockSummaryToExcel(StockSummaryReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Stock Summary");
        int row = WriteReportHeader(ws, "Stock Summary Report", 6, subtitle: $"Report Date: {report.ReportDate:dd MMM yyyy}");

        WriteKpiCard(ws, row, 1, "Total Products", report.TotalProducts.ToString("N0"));
        WriteKpiCard(ws, row, 2, "In Stock", report.ProductsInStock.ToString("N0"), SuccessGreen);
        WriteKpiCard(ws, row, 3, "Out of Stock", report.ProductsOutOfStock.ToString("N0"), DangerRed);
        WriteKpiCard(ws, row, 4, "Below Reorder", report.ProductsBelowReorderLevel.ToString("N0"), WarningOrange);
        WriteKpiCard(ws, row, 5, "Stock Value (USD)", $"${report.TotalStockValueUSD:N2}");
        WriteKpiCard(ws, row, 6, "Stock Value (ZIG)", $"ZIG {report.TotalStockValueZIG:N2}");
        row += 3;

        ws.Cell(row, 1).Value = "Warehouse Code"; ws.Cell(row, 2).Value = "Warehouse Name";
        ws.Cell(row, 3).Value = "Products"; ws.Cell(row, 4).Value = "Total Qty";
        ws.Cell(row, 5).Value = "Value (USD)"; ws.Cell(row, 6).Value = "Value (ZIG)";
        StyleTableHeader(ws, row, 6);
        int freezeAt = row;
        row++;
        int dataStart = row;
        foreach (var wh in report.StockByWarehouse.OrderByDescending(w => w.TotalQuantity))
        {
            ws.Cell(row, 1).Value = wh.WarehouseCode;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = wh.WarehouseName;
            ws.Cell(row, 3).Value = wh.ProductCount; ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 4).Value = wh.TotalQuantity; ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 5).Value = wh.TotalValueUSD; ws.Cell(row, 5).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 6).Value = wh.TotalValueZIG; ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        StyleDataRows(ws, dataStart, row - 1, 6);

        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 3).Value = report.TotalProducts; ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0";
        ws.Cell(row, 4).FormulaA1 = $"SUM(D{dataStart}:D{row - 1})"; ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0";
        ws.Cell(row, 5).Value = report.TotalStockValueUSD; ws.Cell(row, 5).Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell(row, 6).Value = report.TotalStockValueZIG; ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
        StyleTotalsRow(ws, row, 6);

        WriteFooter(ws, row + 1, 6);
        FinalizeSheet(ws, 6, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // PAYMENT SUMMARY
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportPaymentSummaryToExcel(PaymentSummaryReport report)
    {
        using var workbook = new XLWorkbook();

        // ── Dashboard Sheet ──
        var dash = workbook.Worksheets.Add("Payment Dashboard");
        int row = WriteReportHeader(dash, "Payment Summary Report", 5, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total Payments", report.TotalPayments.ToString("N0"));
        WriteKpiCard(dash, row, 2, "Total (USD)", $"${report.TotalAmountUSD:N2}");
        WriteKpiCard(dash, row, 3, "Total (ZIG)", $"ZIG {report.TotalAmountZIG:N2}");
        row += 3;

        dash.Range(row, 1, row, 5).Merge();
        dash.Cell(row, 1).Value = "PAYMENT METHODS BREAKDOWN";
        dash.Cell(row, 1).Style.Font.Bold = true;
        dash.Cell(row, 1).Style.Font.FontSize = 11;
        dash.Cell(row, 1).Style.Font.FontColor = LightNavy;
        row++;

        dash.Cell(row, 1).Value = "Payment Method"; dash.Cell(row, 2).Value = "Count";
        dash.Cell(row, 3).Value = "Amount (USD)"; dash.Cell(row, 4).Value = "Amount (ZIG)";
        dash.Cell(row, 5).Value = "% of Total";
        StyleTableHeader(dash, row, 5);
        row++;
        int dataStart = row;
        foreach (var m in report.PaymentsByMethod.OrderByDescending(x => x.TotalAmountUSD))
        {
            dash.Cell(row, 1).Value = m.PaymentMethod;
            dash.Cell(row, 1).Style.Font.Bold = true;
            dash.Cell(row, 2).Value = m.PaymentCount; dash.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            dash.Cell(row, 3).Value = m.TotalAmountUSD; dash.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
            dash.Cell(row, 4).Value = m.TotalAmountZIG; dash.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            dash.Cell(row, 5).Value = m.PercentageOfTotal / 100; dash.Cell(row, 5).Style.NumberFormat.Format = "0.0%";
            dash.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;
        }
        StyleDataRows(dash, dataStart, row - 1, 5);

        dash.Cell(row, 1).Value = "TOTAL";
        dash.Cell(row, 2).Value = report.TotalPayments; dash.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        dash.Cell(row, 3).Value = report.TotalAmountUSD; dash.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
        dash.Cell(row, 4).Value = report.TotalAmountZIG; dash.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
        dash.Cell(row, 5).Value = 1.0; dash.Cell(row, 5).Style.NumberFormat.Format = "0.0%";
        dash.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(dash, row, 5);

        WriteFooter(dash, row + 1, 5);
        FinalizeSheet(dash, 5, landscape: true);

        // ── Daily Payments Sheet ──
        var daily = workbook.Worksheets.Add("Daily Payments");
        int dRow = WriteReportHeader(daily, "Daily Payments Breakdown", 4, report.FromDate, report.ToDate);

        daily.Cell(dRow, 1).Value = "Date"; daily.Cell(dRow, 2).Value = "Count";
        daily.Cell(dRow, 3).Value = "Amount (USD)"; daily.Cell(dRow, 4).Value = "Amount (ZIG)";
        StyleTableHeader(daily, dRow, 4);
        int freezeAt = dRow;
        dRow++;
        int dailyStart = dRow;
        foreach (var d in report.DailyPayments.OrderByDescending(d => d.Date))
        {
            daily.Cell(dRow, 1).Value = d.Date.ToString("ddd, dd MMM yyyy");
            daily.Cell(dRow, 2).Value = d.PaymentCount; daily.Cell(dRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            daily.Cell(dRow, 3).Value = d.TotalAmountUSD; daily.Cell(dRow, 3).Style.NumberFormat.Format = "$#,##0.00";
            daily.Cell(dRow, 4).Value = d.TotalAmountZIG; daily.Cell(dRow, 4).Style.NumberFormat.Format = "#,##0.00";
            dRow++;
        }
        StyleDataRows(daily, dailyStart, dRow - 1, 4);

        daily.Cell(dRow, 1).Value = "TOTAL";
        daily.Cell(dRow, 2).Value = report.TotalPayments; daily.Cell(dRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        daily.Cell(dRow, 3).Value = report.TotalAmountUSD; daily.Cell(dRow, 3).Style.NumberFormat.Format = "$#,##0.00";
        daily.Cell(dRow, 4).Value = report.TotalAmountZIG; daily.Cell(dRow, 4).Style.NumberFormat.Format = "#,##0.00";
        StyleTotalsRow(daily, dRow, 4);

        WriteFooter(daily, dRow + 1, 4);
        FinalizeSheet(daily, 4, freezeAt);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // TOP CUSTOMERS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportTopCustomersToExcel(TopCustomersReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Top Customers");
        int row = WriteReportHeader(ws, "Top Customers Report", 8, report.FromDate, report.ToDate);

        WriteKpiCard(ws, row, 1, "Total Customers", report.TotalCustomers.ToString("N0"));
        WriteKpiCard(ws, row, 2, "Customers Listed", report.TopCustomers.Count.ToString("N0"));
        WriteKpiCard(ws, row, 3, "Total Purchases (USD)", $"${report.TopCustomers.Sum(c => c.TotalPurchasesUSD):N2}");
        WriteKpiCard(ws, row, 4, "Total Outstanding (USD)", $"${report.TopCustomers.Sum(c => c.OutstandingBalanceUSD):N2}", DangerRed);
        row += 3;

        ws.Cell(row, 1).Value = "Rank"; ws.Cell(row, 2).Value = "Code"; ws.Cell(row, 3).Value = "Customer Name";
        ws.Cell(row, 4).Value = "Invoices"; ws.Cell(row, 5).Value = "Purchases (USD)";
        ws.Cell(row, 6).Value = "Purchases (ZIG)"; ws.Cell(row, 7).Value = "Payments (USD)";
        ws.Cell(row, 8).Value = "Balance (USD)";
        StyleTableHeader(ws, row, 8);
        int freezeAt = row;
        row++;
        int dataStart = row;
        foreach (var c in report.TopCustomers)
        {
            ws.Cell(row, 1).Value = c.Rank; ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (c.Rank <= 3) ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = c.CardCode;
            ws.Cell(row, 3).Value = c.CardName;
            ws.Cell(row, 4).Value = c.InvoiceCount; ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = c.TotalPurchasesUSD; ws.Cell(row, 5).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 6).Value = c.TotalPurchasesZIG; ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 7).Value = c.TotalPaymentsUSD; ws.Cell(row, 7).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 8).Value = c.OutstandingBalanceUSD; ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";
            if (c.OutstandingBalanceUSD > 0)
            {
                ws.Cell(row, 8).Style.Font.FontColor = DangerRed;
                ws.Cell(row, 8).Style.Font.Bold = true;
            }
            else
            {
                ws.Cell(row, 8).Style.Font.FontColor = SuccessGreen;
            }
            row++;
        }
        StyleDataRows(ws, dataStart, row - 1, 8);

        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 4).Value = report.TopCustomers.Sum(c => c.InvoiceCount); ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 5).Value = report.TopCustomers.Sum(c => c.TotalPurchasesUSD); ws.Cell(row, 5).Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell(row, 6).Value = report.TopCustomers.Sum(c => c.TotalPurchasesZIG); ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 7).Value = report.TopCustomers.Sum(c => c.TotalPaymentsUSD); ws.Cell(row, 7).Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell(row, 8).Value = report.TopCustomers.Sum(c => c.OutstandingBalanceUSD); ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";
        StyleTotalsRow(ws, row, 8);

        WriteFooter(ws, row + 1, 8);
        FinalizeSheet(ws, 8, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // LOW STOCK ALERTS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportLowStockAlertsToExcel(LowStockAlertReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Low Stock Alerts");
        int row = WriteReportHeader(ws, "Low Stock Alerts Report", 7, subtitle: $"Report Date: {report.ReportDate:dd MMM yyyy}");

        WriteKpiCard(ws, row, 1, "Total Alerts", report.TotalAlerts.ToString("N0"));
        WriteKpiCard(ws, row, 2, "Critical", report.CriticalCount.ToString("N0"), DangerRed);
        WriteKpiCard(ws, row, 3, "Warning", report.WarningCount.ToString("N0"), WarningOrange);
        row += 3;

        ws.Cell(row, 1).Value = "Alert Level"; ws.Cell(row, 2).Value = "Item Code"; ws.Cell(row, 3).Value = "Item Name";
        ws.Cell(row, 4).Value = "Warehouse"; ws.Cell(row, 5).Value = "Current Stock";
        ws.Cell(row, 6).Value = "Reorder Level"; ws.Cell(row, 7).Value = "Suggested Order";
        StyleTableHeader(ws, row, 7);
        int freezeAt = row;
        row++;
        int dataStart = row;
        foreach (var item in report.Items.OrderBy(i => i.AlertLevel == "Critical" ? 0 : 1).ThenBy(i => i.CurrentStock))
        {
            ws.Cell(row, 1).Value = item.AlertLevel.ToUpper();
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (item.AlertLevel == "Critical")
            {
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.White;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = DangerRed;
            }
            else
            {
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.Black;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#fff3cd");
            }
            ws.Cell(row, 2).Value = item.ItemCode;
            ws.Cell(row, 3).Value = item.ItemName;
            ws.Cell(row, 4).Value = item.WarehouseCode;
            ws.Cell(row, 5).Value = item.CurrentStock; ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 5).Style.Font.Bold = true;
            if (item.CurrentStock <= 0) ws.Cell(row, 5).Style.Font.FontColor = DangerRed;
            else ws.Cell(row, 5).Style.Font.FontColor = WarningOrange;
            ws.Cell(row, 6).Value = item.ReorderLevel; ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 7).Value = item.SuggestedReorderQty; ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 7).Style.Font.FontColor = XLColor.FromHtml("#1565c0");
            ws.Cell(row, 7).Style.Font.Bold = true;
            row++;
        }
        StyleDataRows(ws, dataStart, row - 1, 7);

        WriteFooter(ws, row, 7);
        FinalizeSheet(ws, 7, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // ORDER FULFILLMENT
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportOrderFulfillmentToExcel(OrderFulfillmentReport report)
    {
        using var workbook = new XLWorkbook();

        // ── Dashboard Sheet ──
        var dash = workbook.Worksheets.Add("Fulfillment Dashboard");
        int row = WriteReportHeader(dash, "Order Fulfillment Report", 4, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total Orders", report.TotalOrders.ToString("N0"));
        WriteKpiCard(dash, row, 2, "Fulfillment Rate", $"{report.FulfillmentRatePercent:N1}%", report.FulfillmentRatePercent >= 80 ? SuccessGreen : DangerRed);
        WriteKpiCard(dash, row, 3, "Open Orders", report.OpenOrders.ToString("N0"), WarningOrange);
        WriteKpiCard(dash, row, 4, "Pending Value (USD)", $"${report.TotalPendingValueUSD:N2}", DangerRed);
        row += 3;

        dash.Range(row, 1, row, 4).Merge();
        dash.Cell(row, 1).Value = "KEY METRICS";
        dash.Cell(row, 1).Style.Font.Bold = true; dash.Cell(row, 1).Style.Font.FontSize = 11; dash.Cell(row, 1).Style.Font.FontColor = LightNavy;
        row++;

        WriteKpiRow(dash, row, "Total Orders", report.TotalOrders.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Open Orders", report.OpenOrders.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Closed Orders", report.ClosedOrders.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Cancelled Orders", report.CancelledOrders.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Fulfillment Rate", $"{report.FulfillmentRatePercent:N1}%", true); row++;
        WriteKpiRow(dash, row, "Total Order Value (USD)", $"${report.TotalOrderValueUSD:N2}", true); row++;
        WriteKpiRow(dash, row, "Total Order Value (ZIG)", $"ZIG {report.TotalOrderValueZIG:N2}"); row++;
        WriteKpiRow(dash, row, "Delivered Value (USD)", $"${report.TotalDeliveredValueUSD:N2}"); row++;
        WriteKpiRow(dash, row, "Pending Value (USD)", $"${report.TotalPendingValueUSD:N2}"); row++;
        WriteKpiRow(dash, row, "Total Line Items", report.TotalLineItems.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Fully Delivered Lines", report.FullyDeliveredLines.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Partially Delivered Lines", report.PartiallyDeliveredLines.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Undelivered Lines", report.UndeliveredLines.ToString("N0")); row++;

        WriteFooter(dash, row, 4);
        FinalizeSheet(dash, 4);

        // ── Orders Detail Sheet ──
        var ws = workbook.Worksheets.Add("Order Details");
        int oRow = WriteReportHeader(ws, "Order Details", 12, report.FromDate, report.ToDate);

        ws.Cell(oRow, 1).Value = "Order#"; ws.Cell(oRow, 2).Value = "Date"; ws.Cell(oRow, 3).Value = "Due Date";
        ws.Cell(oRow, 4).Value = "Customer"; ws.Cell(oRow, 5).Value = "Currency"; ws.Cell(oRow, 6).Value = "Total";
        ws.Cell(oRow, 7).Value = "Status"; ws.Cell(oRow, 8).Value = "Qty Ordered"; ws.Cell(oRow, 9).Value = "Qty Delivered";
        ws.Cell(oRow, 10).Value = "Qty Pending"; ws.Cell(oRow, 11).Value = "Fulfillment %"; ws.Cell(oRow, 12).Value = "Overdue";
        StyleTableHeader(ws, oRow, 12);
        int freezeAt = oRow;
        oRow++;
        int dataStart = oRow;
        foreach (var o in report.Orders)
        {
            ws.Cell(oRow, 1).Value = o.DocNum;
            ws.Cell(oRow, 2).Value = o.OrderDate.ToString("dd MMM yyyy");
            ws.Cell(oRow, 3).Value = o.DueDate.ToString("dd MMM yyyy");
            ws.Cell(oRow, 4).Value = $"{o.CardName} ({o.CardCode})";
            ws.Cell(oRow, 5).Value = o.DocCurrency;
            ws.Cell(oRow, 6).Value = o.OrderTotal; ws.Cell(oRow, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(oRow, 7).Value = o.Status;
            ws.Cell(oRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(oRow, 8).Value = o.TotalQuantityOrdered; ws.Cell(oRow, 8).Style.NumberFormat.Format = "#,##0";
            ws.Cell(oRow, 9).Value = o.TotalQuantityDelivered; ws.Cell(oRow, 9).Style.NumberFormat.Format = "#,##0";
            ws.Cell(oRow, 10).Value = o.TotalQuantityPending; ws.Cell(oRow, 10).Style.NumberFormat.Format = "#,##0";
            ws.Cell(oRow, 11).Value = o.FulfillmentPercent / 100; ws.Cell(oRow, 11).Style.NumberFormat.Format = "0.0%";
            ws.Cell(oRow, 12).Value = o.IsOverdue ? $"YES ({o.DaysOverdue}d)" : "No";
            if (o.IsOverdue)
            {
                ws.Cell(oRow, 12).Style.Font.FontColor = DangerRed;
                ws.Cell(oRow, 12).Style.Font.Bold = true;
            }
            oRow++;
        }
        StyleDataRows(ws, dataStart, oRow - 1, 12);

        WriteFooter(ws, oRow, 12);
        FinalizeSheet(ws, 12, freezeAt, landscape: true);

        // ── By Customer Sheet ──
        if (report.FulfillmentByCustomer.Any())
        {
            var cws = workbook.Worksheets.Add("By Customer");
            int cRow = WriteReportHeader(cws, "Fulfillment by Customer", 8, report.FromDate, report.ToDate);

            cws.Cell(cRow, 1).Value = "Customer"; cws.Cell(cRow, 2).Value = "Code"; cws.Cell(cRow, 3).Value = "Total Orders";
            cws.Cell(cRow, 4).Value = "Open"; cws.Cell(cRow, 5).Value = "Closed"; cws.Cell(cRow, 6).Value = "Order Value (USD)";
            cws.Cell(cRow, 7).Value = "Fulfillment %"; cws.Cell(cRow, 8).Value = "Pending Value (USD)";
            StyleTableHeader(cws, cRow, 8);
            int cFreeze = cRow;
            cRow++;
            int cStart = cRow;
            foreach (var c in report.FulfillmentByCustomer.OrderByDescending(x => x.TotalOrderValue))
            {
                cws.Cell(cRow, 1).Value = c.CardName;
                cws.Cell(cRow, 2).Value = c.CardCode;
                cws.Cell(cRow, 3).Value = c.TotalOrders; cws.Cell(cRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cws.Cell(cRow, 4).Value = c.OpenOrders; cws.Cell(cRow, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cws.Cell(cRow, 5).Value = c.ClosedOrders; cws.Cell(cRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cws.Cell(cRow, 6).Value = c.TotalOrderValue; cws.Cell(cRow, 6).Style.NumberFormat.Format = "$#,##0.00";
                cws.Cell(cRow, 7).Value = c.FulfillmentRatePercent / 100; cws.Cell(cRow, 7).Style.NumberFormat.Format = "0.0%";
                cws.Cell(cRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cws.Cell(cRow, 8).Value = c.TotalPendingValue; cws.Cell(cRow, 8).Style.NumberFormat.Format = "$#,##0.00";
                if (c.TotalPendingValue > 0) cws.Cell(cRow, 8).Style.Font.FontColor = DangerRed;
                cRow++;
            }
            StyleDataRows(cws, cStart, cRow - 1, 8);

            cws.Cell(cRow, 1).Value = "TOTAL";
            cws.Cell(cRow, 3).Value = report.FulfillmentByCustomer.Sum(c => c.TotalOrders); cws.Cell(cRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cws.Cell(cRow, 6).Value = report.FulfillmentByCustomer.Sum(c => c.TotalOrderValue); cws.Cell(cRow, 6).Style.NumberFormat.Format = "$#,##0.00";
            cws.Cell(cRow, 8).Value = report.FulfillmentByCustomer.Sum(c => c.TotalPendingValue); cws.Cell(cRow, 8).Style.NumberFormat.Format = "$#,##0.00";
            StyleTotalsRow(cws, cRow, 8);

            WriteFooter(cws, cRow + 1, 8);
            FinalizeSheet(cws, 8, cFreeze, landscape: true);
        }

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // CREDIT NOTES
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportCreditNoteSummaryToExcel(CreditNoteSummaryReport report)
    {
        using var workbook = new XLWorkbook();

        var dash = workbook.Worksheets.Add("Credit Notes Dashboard");
        int row = WriteReportHeader(dash, "Credit Notes Summary Report", 5, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total Credit Notes", report.TotalCreditNotes.ToString("N0"));
        WriteKpiCard(dash, row, 2, "Total (USD)", $"${report.TotalCreditAmountUSD:N2}", DangerRed);
        WriteKpiCard(dash, row, 3, "Total (ZIG)", $"ZIG {report.TotalCreditAmountZIG:N2}");
        WriteKpiCard(dash, row, 4, "Avg Value (USD)", $"${report.AverageCreditNoteValueUSD:N2}");
        WriteKpiCard(dash, row, 5, "Credit-to-Sales", $"{report.CreditToSalesRatioPercent:N1}%", report.CreditToSalesRatioPercent > 5 ? DangerRed : SuccessGreen);
        row += 3;

        WriteKpiRow(dash, row, "Total Credit Notes", report.TotalCreditNotes.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Total Amount (USD)", $"${report.TotalCreditAmountUSD:N2}", true); row++;
        WriteKpiRow(dash, row, "Total Amount (ZIG)", $"ZIG {report.TotalCreditAmountZIG:N2}"); row++;
        WriteKpiRow(dash, row, "VAT (USD)", $"${report.TotalVatUSD:N2}"); row++;
        WriteKpiRow(dash, row, "Avg Credit Note (USD)", $"${report.AverageCreditNoteValueUSD:N2}"); row++;
        WriteKpiRow(dash, row, "Unique Customers", report.UniqueCustomers.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Credit-to-Sales Ratio", $"{report.CreditToSalesRatioPercent:N1}%", true); row++;

        WriteFooter(dash, row, 5);
        FinalizeSheet(dash, 5);

        if (report.ByCustomer.Any())
        {
            var cws = workbook.Worksheets.Add("By Customer");
            int cRow = WriteReportHeader(cws, "Credit Notes by Customer", 5, report.FromDate, report.ToDate);

            cws.Cell(cRow, 1).Value = "Customer Code"; cws.Cell(cRow, 2).Value = "Customer Name"; cws.Cell(cRow, 3).Value = "Count";
            cws.Cell(cRow, 4).Value = "Amount (USD)"; cws.Cell(cRow, 5).Value = "Amount (ZIG)";
            StyleTableHeader(cws, cRow, 5);
            int cFreeze = cRow;
            cRow++;
            int cStart = cRow;
            foreach (var c in report.ByCustomer.OrderByDescending(x => x.TotalAmountUSD))
            {
                cws.Cell(cRow, 1).Value = c.CardCode;
                cws.Cell(cRow, 2).Value = c.CardName;
                cws.Cell(cRow, 3).Value = c.CreditNoteCount; cws.Cell(cRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cws.Cell(cRow, 4).Value = c.TotalAmountUSD; cws.Cell(cRow, 4).Style.NumberFormat.Format = "$#,##0.00";
                cws.Cell(cRow, 5).Value = c.TotalAmountZIG; cws.Cell(cRow, 5).Style.NumberFormat.Format = "#,##0.00";
                cRow++;
            }
            StyleDataRows(cws, cStart, cRow - 1, 5);

            cws.Cell(cRow, 1).Value = "TOTAL";
            cws.Cell(cRow, 3).Value = report.ByCustomer.Sum(c => c.CreditNoteCount); cws.Cell(cRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cws.Cell(cRow, 4).Value = report.ByCustomer.Sum(c => c.TotalAmountUSD); cws.Cell(cRow, 4).Style.NumberFormat.Format = "$#,##0.00";
            cws.Cell(cRow, 5).Value = report.ByCustomer.Sum(c => c.TotalAmountZIG); cws.Cell(cRow, 5).Style.NumberFormat.Format = "#,##0.00";
            StyleTotalsRow(cws, cRow, 5);

            WriteFooter(cws, cRow + 1, 5);
            FinalizeSheet(cws, 5, cFreeze);
        }

        if (report.TopProductsReturned.Any())
        {
            var pws = workbook.Worksheets.Add("Products Returned");
            int pRow = WriteReportHeader(pws, "Top Products Returned", 5, report.FromDate, report.ToDate);

            pws.Cell(pRow, 1).Value = "Item Code"; pws.Cell(pRow, 2).Value = "Product Name";
            pws.Cell(pRow, 3).Value = "Qty Returned"; pws.Cell(pRow, 4).Value = "Value (USD)"; pws.Cell(pRow, 5).Value = "Times Returned";
            StyleTableHeader(pws, pRow, 5);
            int pFreeze = pRow;
            pRow++;
            int pStart = pRow;
            foreach (var p in report.TopProductsReturned.OrderByDescending(x => x.TotalCreditAmountUSD))
            {
                pws.Cell(pRow, 1).Value = p.ItemCode;
                pws.Cell(pRow, 2).Value = p.ItemName;
                pws.Cell(pRow, 3).Value = p.TotalQuantityReturned; pws.Cell(pRow, 3).Style.NumberFormat.Format = "#,##0";
                pws.Cell(pRow, 4).Value = p.TotalCreditAmountUSD; pws.Cell(pRow, 4).Style.NumberFormat.Format = "$#,##0.00";
                pws.Cell(pRow, 5).Value = p.TimesReturned;
                pRow++;
            }
            StyleDataRows(pws, pStart, pRow - 1, 5);

            pws.Cell(pRow, 1).Value = "TOTAL";
            pws.Cell(pRow, 3).Value = report.TopProductsReturned.Sum(p => p.TotalQuantityReturned); pws.Cell(pRow, 3).Style.NumberFormat.Format = "#,##0";
            pws.Cell(pRow, 4).Value = report.TopProductsReturned.Sum(p => p.TotalCreditAmountUSD); pws.Cell(pRow, 4).Style.NumberFormat.Format = "$#,##0.00";
            StyleTotalsRow(pws, pRow, 5);

            WriteFooter(pws, pRow + 1, 5);
            FinalizeSheet(pws, 5, pFreeze);
        }

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // PURCHASE ORDERS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportPurchaseOrderSummaryToExcel(PurchaseOrderSummaryReport report)
    {
        using var workbook = new XLWorkbook();

        var dash = workbook.Worksheets.Add("Purchasing Dashboard");
        int row = WriteReportHeader(dash, "Purchase Orders Summary Report", 5, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total POs", report.TotalPurchaseOrders.ToString("N0"));
        WriteKpiCard(dash, row, 2, "Total Value (USD)", $"${report.TotalOrderValueUSD:N2}");
        WriteKpiCard(dash, row, 3, "Open POs", report.OpenOrders.ToString("N0"), WarningOrange);
        WriteKpiCard(dash, row, 4, "Pending Value (USD)", $"${report.TotalPendingValueUSD:N2}", DangerRed);
        WriteKpiCard(dash, row, 5, "Unique Suppliers", report.UniqueSuppliers.ToString("N0"));
        row += 3;

        WriteKpiRow(dash, row, "Total Purchase Orders", report.TotalPurchaseOrders.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Open Orders", report.OpenOrders.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Closed Orders", report.ClosedOrders.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Cancelled Orders", report.CancelledOrders.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Total Value (USD)", $"${report.TotalOrderValueUSD:N2}", true); row++;
        WriteKpiRow(dash, row, "Total Value (ZIG)", $"ZIG {report.TotalOrderValueZIG:N2}"); row++;
        WriteKpiRow(dash, row, "Pending Value (USD)", $"${report.TotalPendingValueUSD:N2}"); row++;
        WriteKpiRow(dash, row, "Avg Order Value (USD)", $"${report.AverageOrderValueUSD:N2}"); row++;

        WriteFooter(dash, row, 5);
        FinalizeSheet(dash, 5);

        if (report.BySupplier.Any())
        {
            var sws = workbook.Worksheets.Add("By Supplier");
            int sRow = WriteReportHeader(sws, "Purchase Orders by Supplier", 7, report.FromDate, report.ToDate);

            sws.Cell(sRow, 1).Value = "Supplier Code"; sws.Cell(sRow, 2).Value = "Supplier Name"; sws.Cell(sRow, 3).Value = "POs";
            sws.Cell(sRow, 4).Value = "Total (USD)"; sws.Cell(sRow, 5).Value = "Total (ZIG)";
            sws.Cell(sRow, 6).Value = "Open POs"; sws.Cell(sRow, 7).Value = "Pending (USD)";
            StyleTableHeader(sws, sRow, 7);
            int sFreeze = sRow;
            sRow++;
            int sStart = sRow;
            foreach (var s in report.BySupplier.OrderByDescending(x => x.TotalValueUSD))
            {
                sws.Cell(sRow, 1).Value = s.CardCode;
                sws.Cell(sRow, 2).Value = s.CardName;
                sws.Cell(sRow, 3).Value = s.OrderCount; sws.Cell(sRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                sws.Cell(sRow, 4).Value = s.TotalValueUSD; sws.Cell(sRow, 4).Style.NumberFormat.Format = "$#,##0.00";
                sws.Cell(sRow, 5).Value = s.TotalValueZIG; sws.Cell(sRow, 5).Style.NumberFormat.Format = "#,##0.00";
                sws.Cell(sRow, 6).Value = s.OpenOrders; sws.Cell(sRow, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                sws.Cell(sRow, 7).Value = s.PendingValueUSD; sws.Cell(sRow, 7).Style.NumberFormat.Format = "$#,##0.00";
                if (s.PendingValueUSD > 0) sws.Cell(sRow, 7).Style.Font.FontColor = DangerRed;
                sRow++;
            }
            StyleDataRows(sws, sStart, sRow - 1, 7);

            sws.Cell(sRow, 1).Value = "TOTAL";
            sws.Cell(sRow, 3).Value = report.BySupplier.Sum(s => s.OrderCount); sws.Cell(sRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sws.Cell(sRow, 4).Value = report.BySupplier.Sum(s => s.TotalValueUSD); sws.Cell(sRow, 4).Style.NumberFormat.Format = "$#,##0.00";
            sws.Cell(sRow, 5).Value = report.BySupplier.Sum(s => s.TotalValueZIG); sws.Cell(sRow, 5).Style.NumberFormat.Format = "#,##0.00";
            sws.Cell(sRow, 7).Value = report.BySupplier.Sum(s => s.PendingValueUSD); sws.Cell(sRow, 7).Style.NumberFormat.Format = "$#,##0.00";
            StyleTotalsRow(sws, sRow, 7);

            WriteFooter(sws, sRow + 1, 7);
            FinalizeSheet(sws, 7, sFreeze, landscape: true);
        }

        if (report.TopProducts.Any())
        {
            var pws = workbook.Worksheets.Add("Top Products");
            int pRow = WriteReportHeader(pws, "Top Purchased Products", 5, report.FromDate, report.ToDate);

            pws.Cell(pRow, 1).Value = "Item Code"; pws.Cell(pRow, 2).Value = "Product Name";
            pws.Cell(pRow, 3).Value = "Qty Ordered"; pws.Cell(pRow, 4).Value = "Cost (USD)"; pws.Cell(pRow, 5).Value = "Times Ordered";
            StyleTableHeader(pws, pRow, 5);
            int pFreeze = pRow;
            pRow++;
            int pStart = pRow;
            foreach (var p in report.TopProducts)
            {
                pws.Cell(pRow, 1).Value = p.ItemCode;
                pws.Cell(pRow, 2).Value = p.ItemName;
                pws.Cell(pRow, 3).Value = p.TotalQuantityOrdered; pws.Cell(pRow, 3).Style.NumberFormat.Format = "#,##0";
                pws.Cell(pRow, 4).Value = p.TotalCostUSD; pws.Cell(pRow, 4).Style.NumberFormat.Format = "$#,##0.00";
                pws.Cell(pRow, 5).Value = p.TimesOrdered;
                pRow++;
            }
            StyleDataRows(pws, pStart, pRow - 1, 5);

            pws.Cell(pRow, 1).Value = "TOTAL";
            pws.Cell(pRow, 3).Value = report.TopProducts.Sum(p => p.TotalQuantityOrdered); pws.Cell(pRow, 3).Style.NumberFormat.Format = "#,##0";
            pws.Cell(pRow, 4).Value = report.TopProducts.Sum(p => p.TotalCostUSD); pws.Cell(pRow, 4).Style.NumberFormat.Format = "$#,##0.00";
            StyleTotalsRow(pws, pRow, 5);

            WriteFooter(pws, pRow + 1, 5);
            FinalizeSheet(pws, 5, pFreeze);
        }

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // RECEIVABLES AGING
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportReceivablesAgingToExcel(ReceivablesAgingReport report)
    {
        using var workbook = new XLWorkbook();

        var ws = workbook.Worksheets.Add("Aging Summary");
        int row = WriteReportHeader(ws, "Receivables Aging Report", 5, subtitle: $"Report Date: {report.ReportDate:dd MMM yyyy}");

        WriteKpiCard(ws, row, 1, "Total Outstanding (USD)", $"${report.TotalOutstandingUSD:N2}", DangerRed);
        WriteKpiCard(ws, row, 2, "Outstanding (ZIG)", $"ZIG {report.TotalOutstandingZIG:N2}");
        WriteKpiCard(ws, row, 3, "Total Customers", report.TotalCustomers.ToString("N0"));
        WriteKpiCard(ws, row, 4, "Current (0-30d)", $"${report.Current.AmountUSD:N2}", SuccessGreen);
        WriteKpiCard(ws, row, 5, "Over 90 days", $"${report.Over90Days.AmountUSD:N2}", DangerRed);
        row += 3;

        ws.Cell(row, 1).Value = "Aging Bucket"; ws.Cell(row, 2).Value = "Invoices";
        ws.Cell(row, 3).Value = "Amount (USD)"; ws.Cell(row, 4).Value = "Amount (ZIG)"; ws.Cell(row, 5).Value = "% of Total";
        StyleTableHeader(ws, row, 5);
        row++;
        int dataStart = row;

        void WriteBucket(AgingBucket bucket, string label, XLColor? color = null)
        {
            ws.Cell(row, 1).Value = label;
            if (color != null) { ws.Cell(row, 1).Style.Font.FontColor = color; ws.Cell(row, 1).Style.Font.Bold = true; }
            ws.Cell(row, 2).Value = bucket.InvoiceCount; ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 3).Value = bucket.AmountUSD; ws.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 4).Value = bucket.AmountZIG; ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 5).Value = bucket.PercentOfTotal / 100; ws.Cell(row, 5).Style.NumberFormat.Format = "0.0%";
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;
        }

        WriteBucket(report.Current, "Current (0\u201330 days)", SuccessGreen);
        WriteBucket(report.Days31To60, "31\u201360 days", WarningOrange);
        WriteBucket(report.Days61To90, "61\u201390 days", WarningOrange);
        WriteBucket(report.Over90Days, "Over 90 days", DangerRed);
        StyleDataRows(ws, dataStart, row - 1, 5);

        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 2).Value = report.Current.InvoiceCount + report.Days31To60.InvoiceCount + report.Days61To90.InvoiceCount + report.Over90Days.InvoiceCount;
        ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 3).Value = report.TotalOutstandingUSD; ws.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell(row, 4).Value = report.TotalOutstandingZIG; ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 5).Value = 1.0; ws.Cell(row, 5).Style.NumberFormat.Format = "0.0%"; ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(ws, row, 5);

        WriteFooter(ws, row + 1, 5);
        FinalizeSheet(ws, 5);

        if (report.CustomerAging.Any())
        {
            var cws = workbook.Worksheets.Add("Customer Aging Detail");
            int cRow = WriteReportHeader(cws, "Customer Aging Detail", 8, subtitle: $"Report Date: {report.ReportDate:dd MMM yyyy}");

            cws.Cell(cRow, 1).Value = "Customer Code"; cws.Cell(cRow, 2).Value = "Customer Name"; cws.Cell(cRow, 3).Value = "Total Owed (USD)";
            cws.Cell(cRow, 4).Value = "Current (0\u201330)"; cws.Cell(cRow, 5).Value = "31\u201360 days"; cws.Cell(cRow, 6).Value = "61\u201390 days";
            cws.Cell(cRow, 7).Value = "Over 90 days"; cws.Cell(cRow, 8).Value = "Invoices";
            StyleTableHeader(cws, cRow, 8);
            int cFreeze = cRow;
            cRow++;
            int cStart = cRow;
            foreach (var c in report.CustomerAging.OrderByDescending(x => x.TotalOutstandingUSD))
            {
                cws.Cell(cRow, 1).Value = c.CardCode;
                cws.Cell(cRow, 2).Value = c.CardName;
                cws.Cell(cRow, 3).Value = c.TotalOutstandingUSD; cws.Cell(cRow, 3).Style.NumberFormat.Format = "$#,##0.00";
                cws.Cell(cRow, 3).Style.Font.Bold = true;
                cws.Cell(cRow, 4).Value = c.CurrentUSD; cws.Cell(cRow, 4).Style.NumberFormat.Format = "$#,##0.00";
                cws.Cell(cRow, 5).Value = c.Days31To60USD; cws.Cell(cRow, 5).Style.NumberFormat.Format = "$#,##0.00";
                if (c.Days31To60USD > 0) cws.Cell(cRow, 5).Style.Font.FontColor = WarningOrange;
                cws.Cell(cRow, 6).Value = c.Days61To90USD; cws.Cell(cRow, 6).Style.NumberFormat.Format = "$#,##0.00";
                if (c.Days61To90USD > 0) cws.Cell(cRow, 6).Style.Font.FontColor = WarningOrange;
                cws.Cell(cRow, 7).Value = c.Over90DaysUSD; cws.Cell(cRow, 7).Style.NumberFormat.Format = "$#,##0.00";
                if (c.Over90DaysUSD > 0) { cws.Cell(cRow, 7).Style.Font.FontColor = DangerRed; cws.Cell(cRow, 7).Style.Font.Bold = true; }
                cws.Cell(cRow, 8).Value = c.TotalInvoices; cws.Cell(cRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cRow++;
            }
            StyleDataRows(cws, cStart, cRow - 1, 8);

            cws.Cell(cRow, 1).Value = "TOTAL";
            cws.Cell(cRow, 3).Value = report.CustomerAging.Sum(c => c.TotalOutstandingUSD); cws.Cell(cRow, 3).Style.NumberFormat.Format = "$#,##0.00";
            cws.Cell(cRow, 4).Value = report.CustomerAging.Sum(c => c.CurrentUSD); cws.Cell(cRow, 4).Style.NumberFormat.Format = "$#,##0.00";
            cws.Cell(cRow, 5).Value = report.CustomerAging.Sum(c => c.Days31To60USD); cws.Cell(cRow, 5).Style.NumberFormat.Format = "$#,##0.00";
            cws.Cell(cRow, 6).Value = report.CustomerAging.Sum(c => c.Days61To90USD); cws.Cell(cRow, 6).Style.NumberFormat.Format = "$#,##0.00";
            cws.Cell(cRow, 7).Value = report.CustomerAging.Sum(c => c.Over90DaysUSD); cws.Cell(cRow, 7).Style.NumberFormat.Format = "$#,##0.00";
            cws.Cell(cRow, 8).Value = report.CustomerAging.Sum(c => c.TotalInvoices); cws.Cell(cRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            StyleTotalsRow(cws, cRow, 8);

            WriteFooter(cws, cRow + 1, 8);
            FinalizeSheet(cws, 8, cFreeze, landscape: true);
        }

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // PROFIT OVERVIEW
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportProfitOverviewToExcel(ProfitOverviewReport report)
    {
        using var workbook = new XLWorkbook();

        var dash = workbook.Worksheets.Add("Profit & Loss");
        int row = WriteReportHeader(dash, "Profit & Loss Overview", 4, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Net Revenue (USD)", $"${report.NetRevenueUSD:N2}");
        WriteKpiCard(dash, row, 2, "Gross Profit (USD)", $"${report.GrossProfitUSD:N2}", report.GrossProfitUSD >= 0 ? SuccessGreen : DangerRed);
        WriteKpiCard(dash, row, 3, "Gross Margin", $"{report.GrossMarginPercent:N1}%", report.GrossMarginPercent >= 20 ? SuccessGreen : DangerRed);
        WriteKpiCard(dash, row, 4, "Collection Rate", $"{report.CollectionRatePercent:N1}%");
        row += 3;

        dash.Range(row, 1, row, 4).Merge();
        dash.Cell(row, 1).Value = "INCOME STATEMENT";
        dash.Cell(row, 1).Style.Font.Bold = true; dash.Cell(row, 1).Style.Font.FontSize = 12;
        dash.Cell(row, 1).Style.Font.FontColor = LightNavy;
        row++;

        dash.Cell(row, 1).Value = ""; dash.Cell(row, 2).Value = "USD"; dash.Cell(row, 3).Value = "ZIG"; dash.Cell(row, 4).Value = "Notes";
        StyleTableHeader(dash, row, 4);
        row++;

        void PLRow(string label, decimal usd, decimal zig, string notes = "", bool bold = false, XLColor? color = null)
        {
            dash.Cell(row, 1).Value = label;
            dash.Cell(row, 1).Style.Font.Bold = bold;
            if (!bold) dash.Cell(row, 1).Style.Alignment.Indent = 1;

            dash.Cell(row, 2).Value = usd; dash.Cell(row, 2).Style.NumberFormat.Format = "$#,##0.00";
            dash.Cell(row, 3).Value = zig; dash.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
            dash.Cell(row, 4).Value = notes;
            dash.Cell(row, 4).Style.Font.FontSize = 9;
            dash.Cell(row, 4).Style.Font.FontColor = XLColor.FromHtml("#757575");

            if (bold) { dash.Cell(row, 2).Style.Font.Bold = true; dash.Cell(row, 3).Style.Font.Bold = true; }
            if (color != null) dash.Cell(row, 2).Style.Font.FontColor = color;
            row++;
        }

        PLRow("Gross Sales", report.TotalRevenueUSD, report.TotalRevenueZIG, $"{report.TotalInvoices} invoices");
        PLRow("Less: Credit Notes", report.TotalCreditNotesUSD, report.TotalCreditNotesZIG, $"{report.TotalCreditNoteCount} credit notes", color: DangerRed);

        // Net Revenue highlight
        dash.Cell(row, 1).Value = "NET REVENUE";
        dash.Range(row, 1, row, 4).Style.Font.Bold = true;
        dash.Range(row, 1, row, 4).Style.Fill.BackgroundColor = AccentBlue;
        dash.Cell(row, 2).Value = report.NetRevenueUSD; dash.Cell(row, 2).Style.NumberFormat.Format = "$#,##0.00";
        dash.Cell(row, 3).Value = report.NetRevenueZIG; dash.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
        row++;

        PLRow("Less: Purchases (COGS)", report.TotalPurchaseCostUSD, report.TotalPurchaseCostZIG, "Cost of goods sold", color: DangerRed);

        // Gross Profit highlight
        dash.Cell(row, 1).Value = "GROSS PROFIT";
        dash.Range(row, 1, row, 4).Style.Font.Bold = true;
        dash.Range(row, 1, row, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#e8f5e9");
        dash.Cell(row, 2).Value = report.GrossProfitUSD; dash.Cell(row, 2).Style.NumberFormat.Format = "$#,##0.00";
        dash.Cell(row, 2).Style.Font.FontColor = report.GrossProfitUSD >= 0 ? SuccessGreen : DangerRed;
        dash.Cell(row, 3).Value = report.GrossProfitZIG; dash.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
        dash.Cell(row, 4).Value = $"Margin: {report.GrossMarginPercent:N1}%";
        row++;

        row++; // blank
        PLRow("Payments Received", report.TotalCollectedUSD, report.TotalCollectedZIG, $"{report.TotalPayments} payments");
        PLRow("Outstanding Receivables", report.OutstandingReceivablesUSD, report.OutstandingReceivablesZIG, $"Collection Rate: {report.CollectionRatePercent:N1}%", color: DangerRed);

        row++;
        dash.Range(row, 1, row, 4).Merge();
        dash.Cell(row, 1).Value = "OPERATING METRICS";
        dash.Cell(row, 1).Style.Font.Bold = true; dash.Cell(row, 1).Style.Font.FontSize = 11;
        dash.Cell(row, 1).Style.Font.FontColor = LightNavy;
        row++;
        WriteKpiRow(dash, row, "Total Invoices", report.TotalInvoices.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Total Credit Notes", report.TotalCreditNoteCount.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Total Payments", report.TotalPayments.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Unique Customers", report.UniqueCustomers.ToString("N0")); row++;
        WriteKpiRow(dash, row, "Gross Margin %", $"{report.GrossMarginPercent:N1}%", true); row++;
        WriteKpiRow(dash, row, "Collection Rate %", $"{report.CollectionRatePercent:N1}%", true); row++;

        WriteFooter(dash, row, 4);
        FinalizeSheet(dash, 4);

        if (report.MonthlyBreakdown.Any())
        {
            var mws = workbook.Worksheets.Add("Monthly Breakdown");
            int mRow = WriteReportHeader(mws, "Monthly Profit & Loss Breakdown", 8, report.FromDate, report.ToDate);

            mws.Cell(mRow, 1).Value = "Month"; mws.Cell(mRow, 2).Value = "Sales (USD)"; mws.Cell(mRow, 3).Value = "Credit Notes";
            mws.Cell(mRow, 4).Value = "Net Revenue"; mws.Cell(mRow, 5).Value = "Purchases"; mws.Cell(mRow, 6).Value = "Gross Profit";
            mws.Cell(mRow, 7).Value = "Margin %"; mws.Cell(mRow, 8).Value = "Invoices";
            StyleTableHeader(mws, mRow, 8);
            int mFreeze = mRow;
            mRow++;
            int mStart = mRow;
            foreach (var m in report.MonthlyBreakdown.OrderByDescending(x => x.Month))
            {
                var net = m.RevenueUSD - m.CreditNotesUSD;
                var gp = net - m.PurchaseCostUSD;
                var margin = net > 0 ? (gp / net * 100) : 0;

                mws.Cell(mRow, 1).Value = m.Month; mws.Cell(mRow, 1).Style.Font.Bold = true;
                mws.Cell(mRow, 2).Value = m.RevenueUSD; mws.Cell(mRow, 2).Style.NumberFormat.Format = "$#,##0.00";
                mws.Cell(mRow, 3).Value = m.CreditNotesUSD; mws.Cell(mRow, 3).Style.NumberFormat.Format = "$#,##0.00";
                if (m.CreditNotesUSD > 0) mws.Cell(mRow, 3).Style.Font.FontColor = DangerRed;
                mws.Cell(mRow, 4).Value = net; mws.Cell(mRow, 4).Style.NumberFormat.Format = "$#,##0.00";
                mws.Cell(mRow, 5).Value = m.PurchaseCostUSD; mws.Cell(mRow, 5).Style.NumberFormat.Format = "$#,##0.00";
                mws.Cell(mRow, 6).Value = gp; mws.Cell(mRow, 6).Style.NumberFormat.Format = "$#,##0.00";
                mws.Cell(mRow, 6).Style.Font.FontColor = gp >= 0 ? SuccessGreen : DangerRed;
                mws.Cell(mRow, 6).Style.Font.Bold = true;
                mws.Cell(mRow, 7).Value = margin / 100; mws.Cell(mRow, 7).Style.NumberFormat.Format = "0.0%";
                mws.Cell(mRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                mws.Cell(mRow, 8).Value = m.InvoiceCount; mws.Cell(mRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                mRow++;
            }
            StyleDataRows(mws, mStart, mRow - 1, 8);

            mws.Cell(mRow, 1).Value = "TOTAL";
            mws.Cell(mRow, 2).Value = report.TotalRevenueUSD; mws.Cell(mRow, 2).Style.NumberFormat.Format = "$#,##0.00";
            mws.Cell(mRow, 3).Value = report.TotalCreditNotesUSD; mws.Cell(mRow, 3).Style.NumberFormat.Format = "$#,##0.00";
            mws.Cell(mRow, 4).Value = report.NetRevenueUSD; mws.Cell(mRow, 4).Style.NumberFormat.Format = "$#,##0.00";
            mws.Cell(mRow, 5).Value = report.TotalPurchaseCostUSD; mws.Cell(mRow, 5).Style.NumberFormat.Format = "$#,##0.00";
            mws.Cell(mRow, 6).Value = report.GrossProfitUSD; mws.Cell(mRow, 6).Style.NumberFormat.Format = "$#,##0.00";
            var totalMargin = report.NetRevenueUSD > 0 ? report.GrossProfitUSD / report.NetRevenueUSD : 0;
            mws.Cell(mRow, 7).Value = totalMargin; mws.Cell(mRow, 7).Style.NumberFormat.Format = "0.0%";
            mws.Cell(mRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            mws.Cell(mRow, 8).Value = report.TotalInvoices; mws.Cell(mRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            StyleTotalsRow(mws, mRow, 8);

            WriteFooter(mws, mRow + 1, 8);
            FinalizeSheet(mws, 8, mFreeze, landscape: true);
        }

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // SLOW MOVING PRODUCTS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportSlowMovingProductsToExcel(SlowMovingProductsReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Slow Moving Products");
        int row = WriteReportHeader(ws, "Slow Moving Products Report", 6, report.FromDate, report.ToDate,
            $"Threshold: {report.DaysThreshold} days without sales");

        var totalValue = report.Products.Sum(p => p.StockValue);
        WriteKpiCard(ws, row, 1, "Slow Moving Items", report.Products.Count.ToString("N0"));
        WriteKpiCard(ws, row, 2, "Stock Value at Risk", $"${totalValue:N2}", DangerRed);
        WriteKpiCard(ws, row, 3, "Threshold (days)", report.DaysThreshold.ToString("N0"));
        row += 3;

        ws.Cell(row, 1).Value = "Item Code"; ws.Cell(row, 2).Value = "Product Name"; ws.Cell(row, 3).Value = "Current Stock";
        ws.Cell(row, 4).Value = "Last Sale Date"; ws.Cell(row, 5).Value = "Days Since Sale"; ws.Cell(row, 6).Value = "Stock Value (USD)";
        StyleTableHeader(ws, row, 6);
        int freezeAt = row;
        row++;
        int dataStart = row;
        foreach (var p in report.Products.OrderByDescending(x => x.DaysSinceLastSale))
        {
            ws.Cell(row, 1).Value = p.ItemCode;
            ws.Cell(row, 2).Value = p.ItemName;
            ws.Cell(row, 3).Value = p.CurrentStock; ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 4).Value = p.LastSoldDate?.ToString("dd MMM yyyy") ?? "Never";
            if (!p.LastSoldDate.HasValue) ws.Cell(row, 4).Style.Font.FontColor = DangerRed;
            ws.Cell(row, 5).Value = p.DaysSinceLastSale; ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (p.DaysSinceLastSale > 90) { ws.Cell(row, 5).Style.Font.FontColor = DangerRed; ws.Cell(row, 5).Style.Font.Bold = true; }
            else if (p.DaysSinceLastSale > 60) ws.Cell(row, 5).Style.Font.FontColor = WarningOrange;
            ws.Cell(row, 6).Value = p.StockValue; ws.Cell(row, 6).Style.NumberFormat.Format = "$#,##0.00";
            row++;
        }
        StyleDataRows(ws, dataStart, row - 1, 6);

        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 3).Value = report.Products.Sum(p => p.CurrentStock); ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0";
        ws.Cell(row, 6).Value = totalValue; ws.Cell(row, 6).Style.NumberFormat.Format = "$#,##0.00";
        StyleTotalsRow(ws, row, 6);

        WriteFooter(ws, row + 1, 6);
        FinalizeSheet(ws, 6, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportMerchandiserPurchaseOrderReportToExcel(GetMerchandiserPurchaseOrderReportResult report)
    {
        using var workbook = new XLWorkbook();

        var overview = workbook.Worksheets.Add("Overview");
        int row = WriteReportHeader(overview, "Merchandiser Purchase Order Report", 8, report.FromDate, report.ToDate);

        WriteKpiCard(overview, row, 1, "Merchandisers", report.TotalMerchandisers.ToString("N0"));
        WriteKpiCard(overview, row, 2, "Orders", report.TotalOrders.ToString("N0"));
        WriteKpiCard(overview, row, 3, "With PO", report.OrdersWithAttachments.ToString("N0"), SuccessGreen);
        WriteKpiCard(overview, row, 4, "Without PO", report.OrdersWithoutAttachments.ToString("N0"), WarningOrange);
        WriteKpiCard(overview, row, 5, "Attachments", report.TotalAttachments.ToString("N0"));
        WriteKpiCard(overview, row, 6, "Order Value", report.TotalOrderValue.ToString("N2"));
        row += 3;

        overview.Range(row, 1, row, 8).Merge();
        overview.Cell(row, 1).Value = "MERCHANDISER BREAKDOWN";
        overview.Cell(row, 1).Style.Font.Bold = true;
        overview.Cell(row, 1).Style.Font.FontSize = 11;
        overview.Cell(row, 1).Style.Font.FontColor = LightNavy;
        row++;

        if (report.Merchandisers.Any())
        {
            overview.Cell(row, 1).Value = "Username";
            overview.Cell(row, 2).Value = "Full Name";
            overview.Cell(row, 3).Value = "Orders";
            overview.Cell(row, 4).Value = "With PO";
            overview.Cell(row, 5).Value = "Attachments";
            overview.Cell(row, 6).Value = "Synced";
            overview.Cell(row, 7).Value = "Total Value";
            overview.Cell(row, 8).Value = "Latest Activity (CAT)";
            StyleTableHeader(overview, row, 8);
            int freezeAt = row;
            row++;
            int dataStart = row;

            foreach (var merchandiser in report.Merchandisers)
            {
                overview.Cell(row, 1).Value = merchandiser.Username;
                overview.Cell(row, 2).Value = merchandiser.FullName;
                overview.Cell(row, 3).Value = merchandiser.OrderCount;
                overview.Cell(row, 4).Value = merchandiser.OrdersWithAttachments;
                overview.Cell(row, 5).Value = merchandiser.AttachmentCount;
                overview.Cell(row, 6).Value = merchandiser.SyncedOrders;
                overview.Cell(row, 7).Value = merchandiser.TotalOrderValue;
                overview.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
                overview.Cell(row, 8).Value = merchandiser.LatestOrderCreatedAtUtc.HasValue
                    ? FormatCatDateTime(merchandiser.LatestOrderCreatedAtUtc.Value)
                    : "Not available";
                row++;
            }

            StyleDataRows(overview, dataStart, row - 1, 8);
            WriteFooter(overview, row, 8);
            FinalizeSheet(overview, 8, freezeAt, landscape: true);
        }
        else
        {
            overview.Range(row, 1, row, 8).Merge();
            overview.Cell(row, 1).Value = "No merchandiser activity matched the selected filters.";
            overview.Cell(row, 1).Style.Font.Italic = true;
            overview.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#616161");
            WriteFooter(overview, row + 1, 8);
            FinalizeSheet(overview, 8, landscape: true);
        }

        var ordersSheet = workbook.Worksheets.Add("Orders");
        int orderRow = WriteReportHeader(ordersSheet, "Merchandiser Order Register", 16, report.FromDate, report.ToDate);
        ordersSheet.Cell(orderRow, 1).Value = "Order #";
        ordersSheet.Cell(orderRow, 2).Value = "Attachment Ref";
        ordersSheet.Cell(orderRow, 3).Value = "Created (CAT)";
        ordersSheet.Cell(orderRow, 4).Value = "Order Date (CAT)";
        ordersSheet.Cell(orderRow, 5).Value = "Merchandiser";
        ordersSheet.Cell(orderRow, 6).Value = "Customer Code";
        ordersSheet.Cell(orderRow, 7).Value = "Customer Name";
        ordersSheet.Cell(orderRow, 8).Value = "SAP Doc #";
        ordersSheet.Cell(orderRow, 9).Value = "SAP DocEntry";
        ordersSheet.Cell(orderRow, 10).Value = "Status";
        ordersSheet.Cell(orderRow, 11).Value = "Synced";
        ordersSheet.Cell(orderRow, 12).Value = "PO Files";
        ordersSheet.Cell(orderRow, 13).Value = "Currency";
        ordersSheet.Cell(orderRow, 14).Value = "Doc Total";
        ordersSheet.Cell(orderRow, 15).Value = "Line Count";
        ordersSheet.Cell(orderRow, 16).Value = "Total Qty";
        StyleTableHeader(ordersSheet, orderRow, 16);
        int ordersFreeze = orderRow;
        orderRow++;

        if (report.Orders.Any())
        {
            int ordersStart = orderRow;
            foreach (var order in report.Orders)
            {
                ordersSheet.Cell(orderRow, 1).Value = order.OrderNumber;
                ordersSheet.Cell(orderRow, 2).Value = order.AttachmentReference;
                ordersSheet.Cell(orderRow, 3).Value = FormatCatDateTime(order.CreatedAtUtc);
                ordersSheet.Cell(orderRow, 4).Value = FormatCatDate(order.OrderDateUtc);
                ordersSheet.Cell(orderRow, 5).Value = $"{order.MerchandiserFullName} ({order.MerchandiserUsername})";
                ordersSheet.Cell(orderRow, 6).Value = order.CardCode;
                ordersSheet.Cell(orderRow, 7).Value = order.CardName ?? string.Empty;
                ordersSheet.Cell(orderRow, 8).Value = order.SapDocNum?.ToString() ?? "Pending";
                ordersSheet.Cell(orderRow, 9).Value = order.SapDocEntry?.ToString() ?? "Not synced";
                ordersSheet.Cell(orderRow, 10).Value = order.StatusLabel;
                ordersSheet.Cell(orderRow, 11).Value = order.IsSynced ? "Yes" : "No";
                ordersSheet.Cell(orderRow, 12).Value = order.AttachmentCount;
                ordersSheet.Cell(orderRow, 13).Value = order.Currency ?? string.Empty;
                ordersSheet.Cell(orderRow, 14).Value = order.DocTotal;
                ordersSheet.Cell(orderRow, 14).Style.NumberFormat.Format = "#,##0.00";
                ordersSheet.Cell(orderRow, 15).Value = order.ItemCount;
                ordersSheet.Cell(orderRow, 16).Value = order.TotalQuantity;
                ordersSheet.Cell(orderRow, 16).Style.NumberFormat.Format = "#,##0.00";
                row++;
                orderRow++;
            }

            StyleDataRows(ordersSheet, ordersStart, orderRow - 1, 16);
        }
        else
        {
            ordersSheet.Range(orderRow, 1, orderRow, 16).Merge();
            ordersSheet.Cell(orderRow, 1).Value = "No orders matched the selected filters.";
            ordersSheet.Cell(orderRow, 1).Style.Font.Italic = true;
            ordersSheet.Cell(orderRow, 1).Style.Font.FontColor = XLColor.FromHtml("#616161");
        }

        WriteFooter(ordersSheet, orderRow, 16);
        FinalizeSheet(ordersSheet, 16, ordersFreeze, landscape: true);

        var attachmentsSheet = workbook.Worksheets.Add("Attachments");
        int attachmentRow = WriteReportHeader(attachmentsSheet, "Uploaded Purchase Orders", 11, report.FromDate, report.ToDate);
        attachmentsSheet.Cell(attachmentRow, 1).Value = "Order #";
        attachmentsSheet.Cell(attachmentRow, 2).Value = "SAP Doc #";
        attachmentsSheet.Cell(attachmentRow, 3).Value = "Attachment Ref";
        attachmentsSheet.Cell(attachmentRow, 4).Value = "Merchandiser";
        attachmentsSheet.Cell(attachmentRow, 5).Value = "Customer";
        attachmentsSheet.Cell(attachmentRow, 6).Value = "File Name";
        attachmentsSheet.Cell(attachmentRow, 7).Value = "Mime Type";
        attachmentsSheet.Cell(attachmentRow, 8).Value = "Size (bytes)";
        attachmentsSheet.Cell(attachmentRow, 9).Value = "Uploaded (CAT)";
        attachmentsSheet.Cell(attachmentRow, 10).Value = "Uploaded By";
        attachmentsSheet.Cell(attachmentRow, 11).Value = "Description";
        StyleTableHeader(attachmentsSheet, attachmentRow, 11);
        int attachmentsFreeze = attachmentRow;
        attachmentRow++;

        var attachmentDetails = report.Orders
            .SelectMany(order => order.Attachments.Select(attachment => new
            {
                order.OrderNumber,
                order.SapDocNum,
                order.AttachmentReference,
                order.MerchandiserFullName,
                order.MerchandiserUsername,
                order.CardCode,
                order.CardName,
                Attachment = attachment
            }))
            .ToList();

        if (attachmentDetails.Any())
        {
            int attachmentsStart = attachmentRow;
            foreach (var detail in attachmentDetails)
            {
                attachmentsSheet.Cell(attachmentRow, 1).Value = detail.OrderNumber;
                attachmentsSheet.Cell(attachmentRow, 2).Value = detail.SapDocNum?.ToString() ?? "Pending";
                attachmentsSheet.Cell(attachmentRow, 3).Value = detail.AttachmentReference;
                attachmentsSheet.Cell(attachmentRow, 4).Value = $"{detail.MerchandiserFullName} ({detail.MerchandiserUsername})";
                attachmentsSheet.Cell(attachmentRow, 5).Value = $"{detail.CardCode} - {detail.CardName}";
                attachmentsSheet.Cell(attachmentRow, 6).Value = detail.Attachment.FileName;
                attachmentsSheet.Cell(attachmentRow, 7).Value = detail.Attachment.MimeType ?? string.Empty;
                attachmentsSheet.Cell(attachmentRow, 8).Value = detail.Attachment.FileSizeBytes;
                attachmentsSheet.Cell(attachmentRow, 9).Value = FormatCatDateTime(detail.Attachment.UploadedAtUtc);
                attachmentsSheet.Cell(attachmentRow, 10).Value = detail.Attachment.UploadedByUsername ?? string.Empty;
                attachmentsSheet.Cell(attachmentRow, 11).Value = detail.Attachment.Description ?? string.Empty;
                attachmentRow++;
            }

            StyleDataRows(attachmentsSheet, attachmentsStart, attachmentRow - 1, 11);
        }
        else
        {
            attachmentsSheet.Range(attachmentRow, 1, attachmentRow, 11).Merge();
            attachmentsSheet.Cell(attachmentRow, 1).Value = "No uploaded purchase-order attachments were returned for this report.";
            attachmentsSheet.Cell(attachmentRow, 1).Style.Font.Italic = true;
            attachmentsSheet.Cell(attachmentRow, 1).Style.Font.FontColor = XLColor.FromHtml("#616161");
        }

        WriteFooter(attachmentsSheet, attachmentRow, 11);
        FinalizeSheet(attachmentsSheet, 11, attachmentsFreeze, landscape: true);

        var linesSheet = workbook.Worksheets.Add("Order Lines");
        int lineRow = WriteReportHeader(linesSheet, "Merchandiser Order Lines", 12, report.FromDate, report.ToDate);
        linesSheet.Cell(lineRow, 1).Value = "Order #";
        linesSheet.Cell(lineRow, 2).Value = "SAP Doc #";
        linesSheet.Cell(lineRow, 3).Value = "Attachment Ref";
        linesSheet.Cell(lineRow, 4).Value = "Merchandiser";
        linesSheet.Cell(lineRow, 5).Value = "Customer";
        linesSheet.Cell(lineRow, 6).Value = "Line #";
        linesSheet.Cell(lineRow, 7).Value = "Item Code";
        linesSheet.Cell(lineRow, 8).Value = "Description";
        linesSheet.Cell(lineRow, 9).Value = "Qty";
        linesSheet.Cell(lineRow, 10).Value = "Fulfilled";
        linesSheet.Cell(lineRow, 11).Value = "Warehouse";
        linesSheet.Cell(lineRow, 12).Value = "Line Total";
        StyleTableHeader(linesSheet, lineRow, 12);
        int linesFreeze = lineRow;
        lineRow++;

        var lineDetails = report.Orders
            .SelectMany(order => order.Lines.Select(line => new
            {
                order.OrderNumber,
                order.SapDocNum,
                order.AttachmentReference,
                order.MerchandiserFullName,
                order.MerchandiserUsername,
                order.CardCode,
                order.CardName,
                Line = line
            }))
            .ToList();

        if (lineDetails.Any())
        {
            int linesStart = lineRow;
            foreach (var detail in lineDetails)
            {
                linesSheet.Cell(lineRow, 1).Value = detail.OrderNumber;
                linesSheet.Cell(lineRow, 2).Value = detail.SapDocNum?.ToString() ?? "Pending";
                linesSheet.Cell(lineRow, 3).Value = detail.AttachmentReference;
                linesSheet.Cell(lineRow, 4).Value = $"{detail.MerchandiserFullName} ({detail.MerchandiserUsername})";
                linesSheet.Cell(lineRow, 5).Value = $"{detail.CardCode} - {detail.CardName}";
                linesSheet.Cell(lineRow, 6).Value = detail.Line.LineNum;
                linesSheet.Cell(lineRow, 7).Value = detail.Line.ItemCode;
                linesSheet.Cell(lineRow, 8).Value = detail.Line.ItemDescription ?? string.Empty;
                linesSheet.Cell(lineRow, 9).Value = detail.Line.Quantity;
                linesSheet.Cell(lineRow, 9).Style.NumberFormat.Format = "#,##0.00";
                linesSheet.Cell(lineRow, 10).Value = detail.Line.QuantityFulfilled;
                linesSheet.Cell(lineRow, 10).Style.NumberFormat.Format = "#,##0.00";
                linesSheet.Cell(lineRow, 11).Value = detail.Line.WarehouseCode ?? string.Empty;
                linesSheet.Cell(lineRow, 12).Value = detail.Line.LineTotal;
                linesSheet.Cell(lineRow, 12).Style.NumberFormat.Format = "#,##0.00";
                lineRow++;
            }

            StyleDataRows(linesSheet, linesStart, lineRow - 1, 12);
        }
        else
        {
            linesSheet.Range(lineRow, 1, lineRow, 12).Merge();
            linesSheet.Cell(lineRow, 1).Value = "No line items were returned for this report.";
            linesSheet.Cell(lineRow, 1).Style.Font.Italic = true;
            linesSheet.Cell(lineRow, 1).Style.Font.FontColor = XLColor.FromHtml("#616161");
        }

        WriteFooter(linesSheet, lineRow, 12);
        FinalizeSheet(linesSheet, 12, linesFreeze, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // PDF / PRINTABLE HTML
    // ═══════════════════════════════════════════════════════════════
    public string GeneratePrintableHtml(string title, string content, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var generatedAt = CurrentCatNow();
        var period = fromDate.HasValue && toDate.HasValue
            ? $"<p class='period'>Period: {fromDate:dd MMM yyyy} \u2013 {toDate:dd MMM yyyy}</p>"
            : $"<p class='period'>Report Date: {generatedAt:dd MMM yyyy}</p>";

        return $@"<!DOCTYPE html>
<html><head><meta charset='utf-8'/>
<title>{title} - {CompanyName}</title>
<style>
  @page {{ margin: 15mm; }}
  * {{ box-sizing: border-box; }}
  body {{ font-family: 'Segoe UI', Arial, sans-serif; color: #333; margin: 0; padding: 0; }}

  .report-header {{
    border-bottom: 3px solid #1a237e;
    padding-bottom: 12px;
    margin-bottom: 20px;
  }}
  .company-name {{
    font-size: 22px;
    font-weight: 700;
    color: #1a237e;
    margin: 0;
    letter-spacing: 0.5px;
  }}
  .system-name {{
    font-size: 10px;
    color: #757575;
    margin: 2px 0 8px 0;
    font-style: italic;
  }}
  .report-title {{
    font-size: 18px;
    font-weight: 600;
    color: #283593;
    margin: 8px 0 4px 0;
  }}
  .period {{
    color: #616161;
    font-size: 11px;
    margin: 0;
    font-style: italic;
  }}
  .generated {{
    color: #9e9e9e;
    font-size: 9px;
    margin: 2px 0 0 0;
  }}

  .kpi-row {{ display: flex; gap: 12px; margin: 18px 0; flex-wrap: wrap; }}
  .kpi {{
    flex: 1;
    min-width: 120px;
    background: #f0f4ff;
    border-radius: 8px;
    padding: 14px 10px;
    text-align: center;
    border-left: 4px solid #1a237e;
    box-shadow: 0 1px 3px rgba(0,0,0,0.08);
  }}
  .kpi h3 {{ margin: 0; font-size: 22px; color: #1a237e; }}
  .kpi p {{ margin: 4px 0 0; font-size: 10px; color: #616161; text-transform: uppercase; letter-spacing: 0.3px; }}
  .kpi.danger {{ border-color: #c62828; }}
  .kpi.danger h3 {{ color: #c62828; }}
  .kpi.success {{ border-color: #2e7d32; }}
  .kpi.success h3 {{ color: #2e7d32; }}
  .kpi.warning {{ border-color: #e65100; }}
  .kpi.warning h3 {{ color: #e65100; }}

  table {{ width: 100%; border-collapse: collapse; margin: 15px 0; font-size: 11px; }}
  th {{
    background: #1a237e;
    color: white;
    padding: 10px 8px;
    text-align: left;
    font-size: 10px;
    text-transform: uppercase;
    letter-spacing: 0.3px;
  }}
  td {{ padding: 8px; border-bottom: 1px solid #e0e0e0; }}
  tr:nth-child(even) {{ background: #f5f5f5; }}
  tr.totals {{
    background: #e8eaf6 !important;
    font-weight: bold;
    border-top: 2px solid #1a237e;
  }}
  tr.totals td {{ color: #1a237e; padding-top: 10px; }}

  .section-title {{
    font-size: 13px;
    font-weight: 600;
    color: #283593;
    margin: 20px 0 8px 0;
    padding-bottom: 4px;
    border-bottom: 1px solid #e0e0e0;
  }}

  .text-end {{ text-align: right; }}
  .text-center {{ text-align: center; }}
  .text-success {{ color: #2e7d32; }}
  .text-danger {{ color: #c62828; }}
  .text-warning {{ color: #e65100; }}
  .text-info {{ color: #0277bd; }}
  .text-bold {{ font-weight: bold; }}
  .badge {{ display: inline-block; padding: 3px 8px; border-radius: 4px; font-size: 10px; font-weight: 600; }}
  .badge-danger {{ background: #c62828; color: white; }}
  .badge-warning {{ background: #f57f17; color: white; }}
  .badge-success {{ background: #2e7d32; color: white; }}

  .footer {{
    margin-top: 30px;
    padding-top: 10px;
    border-top: 2px solid #1a237e;
    font-size: 9px;
    color: #9e9e9e;
    text-align: center;
  }}
  .footer .confidential {{
    font-weight: 600;
    color: #757575;
    text-transform: uppercase;
    letter-spacing: 0.5px;
  }}
</style></head><body>
<div class='report-header'>
  <p class='company-name'>{CompanyName}</p>
  <p class='system-name'>{SystemName}</p>
  <p class='report-title'>{title}</p>
  {period}
        <p class='generated'>Generated: {generatedAt:dd MMM yyyy HH:mm} CAT</p>
</div>
{content}
<div class='footer'>
  <span class='confidential'>Confidential</span> &bull; {CompanyName} &bull; {SystemName} &bull; Generated {DateTime.Now:dd MMM yyyy HH:mm}
</div>
</body></html>";
    }

    // ═══════════════════════════════════════════════════════════════
    // POD UPLOAD STATUS REPORT
    // ═══════════════════════════════════════════════════════════════

    // Stock-sheet-style accent colors
    private static readonly XLColor StockGreen = XLColor.FromHtml("#006100");
    private static readonly XLColor StockGreenLight = XLColor.FromHtml("#c6efce");
    private static readonly XLColor TotalRed = XLColor.FromHtml("#c00000");
    private static readonly XLColor HeaderGreen = XLColor.FromHtml("#006100");
    private static readonly XLColor PendingAmber = XLColor.FromHtml("#9c5700");
    private static readonly XLColor PendingAmberBg = XLColor.FromHtml("#ffeb9c");

    /// <summary>Stock-sheet style green header row.</summary>
    private static void StyleStockHeader(IXLWorksheet ws, int headerRow, int lastCol)
    {
        var range = ws.Range(headerRow, 1, headerRow, lastCol);
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 9;
        range.Style.Fill.BackgroundColor = HeaderGreen;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = HeaderGreen;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#338033");
        ws.Row(headerRow).Height = 36;
    }

    /// <summary>Applies stock-sheet data styling: thin borders, consistent font, dashes for empty.</summary>
    private static void StyleStockData(IXLWorksheet ws, int firstRow, int lastRow, int lastCol)
    {
        if (lastRow < firstRow) return;
        var range = ws.Range(firstRow, 1, lastRow, lastCol);
        range.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#d9d9d9");
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#bfbfbf");
        range.Style.Font.FontSize = 10;
    }

    public byte[] ExportPodUploadStatusToExcel(PodUploadStatusReport report)
    {
        using var workbook = new XLWorkbook();
        DateTime.TryParse(report.FromDate, out var fromDt);
        DateTime.TryParse(report.ToDate, out var toDt);
        var now = DateTime.Now;

        var totalAmount = report.Items.Sum(i => i.DocTotal);
        var uploadedAmount = report.Items.Where(i => i.HasPod).Sum(i => i.DocTotal);
        var pendingAmount = report.Items.Where(i => !i.HasPod).Sum(i => i.DocTotal);

        // ════════════════════════════════════════════════════════════
        //  POD DASHBOARD — all invoices
        // ════════════════════════════════════════════════════════════
        {
            var ws = workbook.Worksheets.Add("POD Dashboard");
            int lastCol = 8;

            // Row 1: Title + date
            ws.Cell(1, 1).Value = "POD UPLOAD STATUS";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Range(1, 1, 1, 3).Merge();
            ws.Cell(1, lastCol).Value = now.ToString("dd MMM yyyy");
            ws.Cell(1, lastCol).Style.Font.FontSize = 9;
            ws.Cell(1, lastCol).Style.Font.FontColor = XLColor.FromHtml("#808080");
            ws.Cell(1, lastCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Row 2: Headers
            int hRow = 2;
            ws.Cell(hRow, 1).Value = "Invoice #";
            ws.Cell(hRow, 2).Value = "Customer";
            ws.Cell(hRow, 3).Value = "Card Code";
            ws.Cell(hRow, 4).Value = "Invoice Date";
            ws.Cell(hRow, 5).Value = "Amount";
            ws.Cell(hRow, 6).Value = "POD Status";
            ws.Cell(hRow, 7).Value = "Uploaded";
            ws.Cell(hRow, 8).Value = "TOTAL";
            StyleStockHeader(ws, hRow, lastCol);
            int freezeRow = hRow;

            // Data rows
            int row = 3;
            int dataStart = row;
            foreach (var item in report.Items)
            {
                ws.Cell(row, 1).Value = item.DocNum;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = item.CardName ?? "-";
                ws.Cell(row, 3).Value = item.CardCode ?? "-";
                ws.Cell(row, 4).Value = FormatExcelDate(item.DocDate);
                ws.Cell(row, 5).Value = item.DocTotal;
                ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                if (item.HasPod)
                {
                    ws.Cell(row, 6).Value = "Uploaded";
                    ws.Cell(row, 6).Style.Font.FontColor = StockGreen;
                    ws.Cell(row, 6).Style.Fill.BackgroundColor = StockGreenLight;
                }
                else
                {
                    ws.Cell(row, 6).Value = "Pending";
                    ws.Cell(row, 6).Style.Font.FontColor = PendingAmber;
                    ws.Cell(row, 6).Style.Fill.BackgroundColor = PendingAmberBg;
                }
                ws.Cell(row, 6).Style.Font.Bold = true;
                ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                if (item.HasPod && item.PodUploadedAt.HasValue)
                {
                    var uploadStr = item.PodUploadedAt.Value.ToString("dd MMM yyyy HH:mm");
                    var uploaderDisplay = FormatPodUploadedByDisplay(item);
                    if (!string.IsNullOrEmpty(uploaderDisplay) && uploaderDisplay != "-")
                        uploadStr += $" ({uploaderDisplay})";
                    ws.Cell(row, 7).Value = uploadStr;
                }
                else
                {
                    ws.Cell(row, 7).Value = "-";
                    ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }

                // TOTAL column (running amount in bold red)
                ws.Cell(row, 8).Value = item.DocTotal;
                ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 8).Style.Font.Bold = true;
                ws.Cell(row, 8).Style.Font.FontColor = TotalRed;
                ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                row++;
            }
            StyleStockData(ws, dataStart, row - 1, lastCol);

            // Summary row
            ws.Cell(row, 1).Value = "SUMMARY";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 4).Value = $"{report.TotalInvoices} invoices";
            ws.Cell(row, 4).Style.Font.Bold = true;
            ws.Cell(row, 5).Value = totalAmount;
            ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 5).Style.Font.Bold = true;
            ws.Cell(row, 6).Value = $"{report.UploadedCount} / {report.PendingCount}";
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 6).Style.Font.Bold = true;
            ws.Cell(row, 8).Value = totalAmount;
            ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 8).Style.Font.Bold = true;
            ws.Cell(row, 8).Style.Font.FontColor = TotalRed;
            ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            var summaryRange = ws.Range(row, 1, row, lastCol);
            summaryRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            summaryRange.Style.Border.TopBorderColor = HeaderGreen;
            summaryRange.Style.Border.BottomBorder = XLBorderStyleValues.Double;
            summaryRange.Style.Border.BottomBorderColor = HeaderGreen;
            summaryRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#f2f2f2");

            FinalizeSheet(ws, lastCol, freezeRow, landscape: true);
            ws.Column(1).Width = 12;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 14;
            ws.Column(6).Width = 12;
            ws.Column(7).Width = 28;
            ws.Column(8).Width = 14;
        }

        // ════════════════════════════════════════════════════════════
        //  PENDING PODs
        // ════════════════════════════════════════════════════════════
        var pending = report.Items.Where(i => !i.HasPod).OrderBy(i => i.DocDate).ToList();
        if (pending.Any())
        {
            var ws = workbook.Worksheets.Add("Pending PODs");
            int lastCol = 6;

            // Row 1: Title + date
            ws.Cell(1, 1).Value = "PENDING POD UPLOADS";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Range(1, 1, 1, 3).Merge();
            ws.Cell(1, lastCol).Value = now.ToString("dd MMM yyyy");
            ws.Cell(1, lastCol).Style.Font.FontSize = 9;
            ws.Cell(1, lastCol).Style.Font.FontColor = XLColor.FromHtml("#808080");
            ws.Cell(1, lastCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Row 2: Headers
            int hRow = 2;
            ws.Cell(hRow, 1).Value = "Invoice #";
            ws.Cell(hRow, 2).Value = "Customer";
            ws.Cell(hRow, 3).Value = "Card Code";
            ws.Cell(hRow, 4).Value = "Invoice Date";
            ws.Cell(hRow, 5).Value = "Days Aging";
            ws.Cell(hRow, 6).Value = "TOTAL";
            StyleStockHeader(ws, hRow, lastCol);
            int freezeRow = hRow;

            int row = 3;
            int dataStart = row;
            foreach (var item in pending)
            {
                DateTime.TryParse(item.DocDate, out var docDt);
                int daysAging = docDt > DateTime.MinValue ? (int)(now - docDt).TotalDays : 0;

                ws.Cell(row, 1).Value = item.DocNum;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = item.CardName ?? "-";
                ws.Cell(row, 3).Value = item.CardCode ?? "-";
                ws.Cell(row, 4).Value = FormatExcelDate(item.DocDate);
                ws.Cell(row, 5).Value = daysAging;
                ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                if (daysAging > 14)
                {
                    ws.Cell(row, 5).Style.Font.FontColor = XLColor.FromHtml("#c00000");
                    ws.Cell(row, 5).Style.Font.Bold = true;
                }
                else if (daysAging > 7)
                {
                    ws.Cell(row, 5).Style.Font.FontColor = PendingAmber;
                    ws.Cell(row, 5).Style.Font.Bold = true;
                }

                // TOTAL column bold red
                ws.Cell(row, 6).Value = item.DocTotal;
                ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 6).Style.Font.Bold = true;
                ws.Cell(row, 6).Style.Font.FontColor = TotalRed;
                ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                row++;
            }
            StyleStockData(ws, dataStart, row - 1, lastCol);

            // Summary
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 4).Value = $"{pending.Count} invoices";
            ws.Cell(row, 4).Style.Font.Bold = true;
            ws.Cell(row, 6).Value = pendingAmount;
            ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 6).Style.Font.Bold = true;
            ws.Cell(row, 6).Style.Font.FontColor = TotalRed;
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            var summaryRange = ws.Range(row, 1, row, lastCol);
            summaryRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            summaryRange.Style.Border.TopBorderColor = HeaderGreen;
            summaryRange.Style.Border.BottomBorder = XLBorderStyleValues.Double;
            summaryRange.Style.Border.BottomBorderColor = HeaderGreen;
            summaryRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#f2f2f2");

            FinalizeSheet(ws, lastCol, freezeRow);
            ws.Column(1).Width = 12;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 12;
            ws.Column(6).Width = 14;
        }

        // ════════════════════════════════════════════════════════════
        //  UPLOADS BY USER
        // ════════════════════════════════════════════════════════════
        var uploadsByUser = report.Items
            .Where(i => i.HasPod)
            .SelectMany(item => GetPodUploadedByUsers(item).Select(uploader => new { Item = item, Uploader = uploader }))
            .GroupBy(entry => entry.Uploader.Username, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                UploadedBy = group.Key,
                UploadedInvoices = group.Select(entry => entry.Item.DocEntry).Distinct().Count(),
                TotalFiles = group.Sum(entry => entry.Uploader.FileCount),
                TotalAmount = group.GroupBy(entry => entry.Item.DocEntry).Sum(invoiceGroup => invoiceGroup.First().Item.DocTotal),
                LatestUpload = group.Max(entry => entry.Uploader.LatestUploadedAt)
            })
            .OrderByDescending(group => group.UploadedInvoices)
            .ThenBy(group => group.UploadedBy)
            .ToList();

        if (uploadsByUser.Any())
        {
            var ws = workbook.Worksheets.Add("Uploads By User");
            int lastCol = 5;

            ws.Cell(1, 1).Value = "POD UPLOADS BY USER";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Range(1, 1, 1, 3).Merge();
            ws.Cell(1, lastCol).Value = now.ToString("dd MMM yyyy");
            ws.Cell(1, lastCol).Style.Font.FontSize = 9;
            ws.Cell(1, lastCol).Style.Font.FontColor = XLColor.FromHtml("#808080");
            ws.Cell(1, lastCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            int hRow = 2;
            ws.Cell(hRow, 1).Value = "Uploaded By";
            ws.Cell(hRow, 2).Value = "Invoices Covered";
            ws.Cell(hRow, 3).Value = "POD Files";
            ws.Cell(hRow, 4).Value = "Invoice Amount";
            ws.Cell(hRow, 5).Value = "Latest Upload";
            StyleStockHeader(ws, hRow, lastCol);
            int freezeRow = hRow;

            int row = 3;
            int dataStart = row;
            foreach (var group in uploadsByUser)
            {
                ws.Cell(row, 1).Value = group.UploadedBy;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = group.UploadedInvoices;
                ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 3).Value = group.TotalFiles;
                ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 4).Value = group.TotalAmount;
                ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(row, 5).Value = group.LatestUpload.HasValue
                    ? group.LatestUpload.Value.ToString("dd MMM yyyy HH:mm")
                    : "-";

                row++;
            }

            StyleStockData(ws, dataStart, row - 1, lastCol);

            ws.Cell(row, 1).Value = "SUMMARY";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = uploadsByUser.Sum(group => group.UploadedInvoices);
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 3).Value = uploadsByUser.Sum(group => group.TotalFiles);
            ws.Cell(row, 3).Style.Font.Bold = true;
            ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 4).Value = uploadsByUser.Sum(group => group.TotalAmount);
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 4).Style.Font.Bold = true;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, 5).Value = uploadsByUser.Count == 1
                ? uploadsByUser[0].UploadedBy
                : $"{uploadsByUser.Count:N0} uploaders";
            ws.Cell(row, 5).Style.Font.Bold = true;

            var summaryRange = ws.Range(row, 1, row, lastCol);
            summaryRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            summaryRange.Style.Border.TopBorderColor = HeaderGreen;
            summaryRange.Style.Border.BottomBorder = XLBorderStyleValues.Double;
            summaryRange.Style.Border.BottomBorderColor = HeaderGreen;
            summaryRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#f2f2f2");

            FinalizeSheet(ws, lastCol, freezeRow, landscape: true);
            ws.Column(1).Width = 28;
            ws.Column(2).Width = 16;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 16;
            ws.Column(5).Width = 22;
        }

        // ════════════════════════════════════════════════════════════
        //  UPLOADED PODs
        // ════════════════════════════════════════════════════════════
        var uploaded = report.Items.Where(i => i.HasPod).OrderByDescending(i => i.PodUploadedAt).ToList();
        if (uploaded.Any())
        {
            var ws = workbook.Worksheets.Add("Uploaded PODs");
            int lastCol = 7;

            // Row 1: Title + date
            ws.Cell(1, 1).Value = "INVOICES WITH POD UPLOADED";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Range(1, 1, 1, 3).Merge();
            ws.Cell(1, lastCol).Value = now.ToString("dd MMM yyyy");
            ws.Cell(1, lastCol).Style.Font.FontSize = 9;
            ws.Cell(1, lastCol).Style.Font.FontColor = XLColor.FromHtml("#808080");
            ws.Cell(1, lastCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            // Row 2: Headers
            int hRow = 2;
            ws.Cell(hRow, 1).Value = "Invoice #";
            ws.Cell(hRow, 2).Value = "Customer";
            ws.Cell(hRow, 3).Value = "Card Code";
            ws.Cell(hRow, 4).Value = "Invoice Date";
            ws.Cell(hRow, 5).Value = "Uploaded";
            ws.Cell(hRow, 6).Value = "Uploaded By";
            ws.Cell(hRow, 7).Value = "TOTAL";
            StyleStockHeader(ws, hRow, lastCol);
            int freezeRow = hRow;

            int row = 3;
            int dataStart = row;
            foreach (var item in uploaded)
            {
                ws.Cell(row, 1).Value = item.DocNum;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = item.CardName ?? "-";
                ws.Cell(row, 3).Value = item.CardCode ?? "-";
                ws.Cell(row, 4).Value = FormatExcelDate(item.DocDate);
                ws.Cell(row, 5).Value = item.PodUploadedAt.HasValue
                    ? item.PodUploadedAt.Value.ToString("dd MMM yyyy HH:mm")
                    : "-";
                ws.Cell(row, 6).Value = FormatPodUploadedByDisplay(item);
                ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                // TOTAL column bold red
                ws.Cell(row, 7).Value = item.DocTotal;
                ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 7).Style.Font.Bold = true;
                ws.Cell(row, 7).Style.Font.FontColor = TotalRed;
                ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                row++;
            }
            StyleStockData(ws, dataStart, row - 1, lastCol);

            // Summary
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 4).Value = $"{uploaded.Count} invoices";
            ws.Cell(row, 4).Style.Font.Bold = true;
            ws.Cell(row, 7).Value = uploadedAmount;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 7).Style.Font.Bold = true;
            ws.Cell(row, 7).Style.Font.FontColor = TotalRed;
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            var summaryRange = ws.Range(row, 1, row, lastCol);
            summaryRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            summaryRange.Style.Border.TopBorderColor = HeaderGreen;
            summaryRange.Style.Border.BottomBorder = XLBorderStyleValues.Double;
            summaryRange.Style.Border.BottomBorderColor = HeaderGreen;
            summaryRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#f2f2f2");

            FinalizeSheet(ws, lastCol, freezeRow, landscape: true);
            ws.Column(1).Width = 12;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 22;
            ws.Column(6).Width = 14;
            ws.Column(7).Width = 14;
        }

        return WorkbookToBytes(workbook);
    }

    private static IReadOnlyList<PodUploadUserSummary> GetPodUploadedByUsers(PodUploadStatusItem item)
    {
        if (item.PodUploadedByUsers.Count > 0)
            return item.PodUploadedByUsers;

        if (!string.IsNullOrWhiteSpace(item.PodUploadedBy))
        {
            return
            [
                new PodUploadUserSummary
                {
                    Username = item.PodUploadedBy.Trim(),
                    FileCount = item.PodCount > 0 ? item.PodCount : 1,
                    LatestUploadedAt = item.PodUploadedAt
                }
            ];
        }

        return [];
    }

    private static string FormatPodUploadedByDisplay(PodUploadStatusItem item)
    {
        var uploaders = GetPodUploadedByUsers(item)
            .Select(uploader => uploader.Username.Trim())
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return uploaders.Count == 0 ? "-" : string.Join(", ", uploaders);
    }

    // ═══════════════════════════════════════════════════════════════
    // TIMESHEET REPORT  (Stock-sheet style)
    // ═══════════════════════════════════════════════════════════════

    // ── Corporate palette (matches Stock Sheets) ──
    private static readonly XLColor TsNavy = XLColor.FromHtml("#1B3A5C");
    private static readonly XLColor TsHeaderBg = XLColor.FromHtml("#2C5F8A");
    private static readonly XLColor TsSubHeaderBg = XLColor.FromHtml("#E8EEF4");
    private static readonly XLColor TsStripeBg = XLColor.FromHtml("#F5F7FA");
    private static readonly XLColor TsGridColor = XLColor.FromHtml("#C5CED8");
    private static readonly XLColor TsGridLight = XLColor.FromHtml("#DDE3EA");
    private static readonly XLColor TsTextDark = XLColor.FromHtml("#1A1A2E");
    private static readonly XLColor TsTextMuted = XLColor.FromHtml("#5A6A7A");
    private static readonly XLColor TsTotalBg = XLColor.FromHtml("#DCE6F0");
    private static readonly XLColor TsGreen = XLColor.FromHtml("#2E7D32");
    private static readonly XLColor TsOrange = XLColor.FromHtml("#E65100");
    private static readonly XLColor TsRed = XLColor.FromHtml("#C62828");

    public byte[] ExportTimesheetReportToExcel(TimesheetReportResponse report, DateTime? fromDate = null, DateTime? toDate = null)
    {
        using var workbook = new XLWorkbook();
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildTimesheetOverviewSheet(workbook, report, fromDate, toDate, now);

        foreach (var user in report.UserSummaries.OrderByDescending(u => u.TotalVisits))
            BuildTimesheetUserSheet(workbook, user, fromDate, toDate, now);

        return WorkbookToBytes(workbook);
    }

    private static void TsApplyDefaults(IXLWorksheet ws)
    {
        ws.Style.Font.FontName = "Aptos";
        ws.Style.Font.FontSize = 10;
    }

    private static int TsTitleBar(IXLWorksheet ws, string title, int lastCol, DateTime now)
    {
        ws.Row(1).Height = 32;
        var titleRange = ws.Range(1, 1, 1, lastCol);
        titleRange.Style.Fill.BackgroundColor = TsNavy;
        titleRange.Style.Font.FontColor = XLColor.White;
        titleRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        titleRange.Style.Border.BottomBorderColor = XLColor.FromHtml("#4A90C4");

        ws.Cell(1, 1).Value = $" {title}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 13;
        ws.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Cell(1, lastCol).Value = now.ToString("dd MMM yyyy  HH:mm");
        ws.Cell(1, lastCol).Style.Font.FontSize = 9;
        ws.Cell(1, lastCol).Style.Font.Italic = true;
        ws.Cell(1, lastCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(1, lastCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        return 2;
    }

    private static int TsColumnHeaders(IXLWorksheet ws, int row, int lastCol, string[] headers)
    {
        ws.Row(row).Height = 44;
        var range = ws.Range(row, 1, row, lastCol);
        range.Style.Fill.BackgroundColor = TsHeaderBg;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 9;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = TsNavy;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#4A7DAA");

        for (int i = 0; i < headers.Length; i++)
            ws.Cell(row, i + 1).Value = headers[i];

        return row + 1;
    }

    private static void TsDataRow(IXLWorksheet ws, int row, int lastCol, bool isStripe)
    {
        var bg = isStripe ? TsStripeBg : XLColor.White;
        var rowRange = ws.Range(row, 1, row, lastCol);
        rowRange.Style.Fill.BackgroundColor = bg;
        rowRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        rowRange.Style.Border.BottomBorderColor = TsGridLight;
        rowRange.Style.Font.FontSize = 10;
        rowRange.Style.Font.FontColor = TsTextDark;
        for (int c = 1; c <= lastCol; c++)
        {
            ws.Cell(row, c).Style.Border.LeftBorder = XLBorderStyleValues.Thin;
            ws.Cell(row, c).Style.Border.LeftBorderColor = TsGridLight;
            ws.Cell(row, c).Style.Border.RightBorder = XLBorderStyleValues.Thin;
            ws.Cell(row, c).Style.Border.RightBorderColor = TsGridLight;
        }
        ws.Cell(row, 1).Style.Border.LeftBorderColor = TsGridColor;
        ws.Cell(row, lastCol).Style.Border.RightBorderColor = TsGridColor;
    }

    private static void TsSummaryRow(IXLWorksheet ws, int row, int lastCol)
    {
        var range = ws.Range(row, 1, row, lastCol);
        range.Style.Fill.BackgroundColor = TsNavy;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 10;
        range.Style.Border.TopBorder = XLBorderStyleValues.Medium;
        range.Style.Border.TopBorderColor = TsNavy;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        range.Style.Border.OutsideBorderColor = TsNavy;
        ws.Row(row).Height = 26;
    }

    private static void TsDisclaimerRow(IXLWorksheet ws, int row, int lastCol, DateTime now)
    {
        var cell = ws.Cell(row, 1);
        cell.Value = $"This document was auto-generated by the Shop Inventory System on {now:dd MMM yyyy 'at' HH:mm}. Data covers check-in/check-out activity.";
        ws.Range(row, 1, row, lastCol).Merge();
        cell.Style.Font.FontSize = 8;
        cell.Style.Font.Italic = true;
        cell.Style.Font.FontColor = XLColor.FromHtml("#9CA3AF");
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void TsFinalize(IXLWorksheet ws, int lastCol, int freezeRow = 0, int freezeCol = 0)
    {
        ws.Columns(1, lastCol).AdjustToContents();
        for (int c = 1; c <= lastCol; c++)
        {
            if (ws.Column(c).Width > 42) ws.Column(c).Width = 42;
            if (ws.Column(c).Width < 11) ws.Column(c).Width = 11;
        }
        if (freezeRow > 0) ws.SheetView.FreezeRows(freezeRow);
        if (freezeCol > 0) ws.SheetView.FreezeColumns(freezeCol);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetLeft(0.4);
        ws.PageSetup.Margins.SetRight(0.4);
        ws.PageSetup.Margins.SetTop(0.4);
        ws.PageSetup.Margins.SetBottom(0.4);
    }

    private static void TsSectionTitle(IXLWorksheet ws, int row, int lastCol, string title)
    {
        ws.Range(row, 1, row, lastCol).Merge();
        var cell = ws.Cell(row, 1);
        cell.Value = title;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 11;
        cell.Style.Font.FontColor = TsNavy;
        cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.BottomBorderColor = TsGridColor;
    }

    private static int TsKpiStrip(IXLWorksheet ws, int row, int lastCol, params (string Label, string Value, XLColor? Color)[] kpis)
    {
        // Value row
        ws.Row(row).Height = 28;
        var valRange = ws.Range(row, 1, row, lastCol);
        valRange.Style.Fill.BackgroundColor = TsSubHeaderBg;
        valRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        valRange.Style.Border.OutsideBorderColor = TsGridColor;
        valRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        valRange.Style.Border.InsideBorderColor = TsGridLight;

        for (int i = 0; i < kpis.Length && i < lastCol; i++)
        {
            var cell = ws.Cell(row, i + 1);
            cell.Value = kpis[i].Value;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 14;
            cell.Style.Font.FontColor = kpis[i].Color ?? TsNavy;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        // Label row
        row++;
        ws.Row(row).Height = 18;
        var lblRange = ws.Range(row, 1, row, lastCol);
        lblRange.Style.Fill.BackgroundColor = TsSubHeaderBg;
        lblRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        lblRange.Style.Border.OutsideBorderColor = TsGridColor;
        lblRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        lblRange.Style.Border.InsideBorderColor = TsGridLight;
        lblRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        lblRange.Style.Border.BottomBorderColor = TsGridColor;

        for (int i = 0; i < kpis.Length && i < lastCol; i++)
        {
            var cell = ws.Cell(row, i + 1);
            cell.Value = kpis[i].Label;
            cell.Style.Font.FontSize = 8;
            cell.Style.Font.FontColor = TsTextMuted;
            cell.Style.Font.Italic = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        return row + 2;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Overview sheet
    // ═══════════════════════════════════════════════════════════════
    private static void BuildTimesheetOverviewSheet(XLWorkbook workbook, TimesheetReportResponse report, DateTime? fromDate, DateTime? toDate, DateTime now)
    {
        const int lastCol = 8;
        var ws = workbook.Worksheets.Add("Overview");
        TsApplyDefaults(ws);

        var period = fromDate.HasValue && toDate.HasValue
            ? $"TIMESHEET REPORT  \u2014  {fromDate:dd MMM yyyy} to {toDate:dd MMM yyyy}"
            : "TIMESHEET REPORT";
        int row = TsTitleBar(ws, period, lastCol, now);

        // KPI strip
        var totalCompleted = report.UserSummaries.Sum(u => u.CompletedVisits);
        var completionPct = report.TotalVisits > 0 ? (double)totalCompleted / report.TotalVisits * 100 : 0;
        var pctColor = completionPct >= 80 ? TsGreen : completionPct >= 50 ? TsOrange : TsRed;

        var allDays = report.UserSummaries.SelectMany(u => u.DailySummaries).GroupBy(d => d.Date)
            .Select(g => new { Date = g.Key, Visits = g.Sum(x => x.VisitCount) })
            .OrderByDescending(x => x.Visits).FirstOrDefault();
        var allCustomers = report.UserSummaries.SelectMany(u => u.CustomerSummaries).GroupBy(c => c.CustomerCode)
            .Select(g => new { Name = g.First().CustomerName, Visits = g.Sum(x => x.VisitCount) })
            .OrderByDescending(x => x.Visits).FirstOrDefault();

        row = TsKpiStrip(ws, row, lastCol,
            ("Total Visits", report.TotalVisits.ToString("N0"), null),
            ("Completed", totalCompleted.ToString("N0"), null),
            ("Total Hours", $"{report.TotalHours:F1}h", null),
            ("Avg per Visit", FormatHoursExcel(report.AverageVisitMinutes), null),
            ("Merchandisers", report.UserSummaries.Count.ToString("N0"), null),
            ("Completion", $"{completionPct:F0}%", pctColor),
            ("Busiest Day", allDays != null ? allDays.Date.ToString("dd MMM") : "\u2014", null),
            ("Top Customer", allCustomers?.Name ?? "\u2014", null));

        // \u2500\u2500 Merchandiser Performance Table \u2500\u2500
        TsSectionTitle(ws, row, lastCol, "MERCHANDISER PERFORMANCE");
        row += 2;

        string[] headers = ["Merchandiser", "Total Visits", "Completed", "Active", "Total Time", "Avg per Visit", "Shops Visited", "Completion"];
        row = TsColumnHeaders(ws, row, lastCol, headers);

        int idx = 0;
        foreach (var user in report.UserSummaries.OrderByDescending(u => u.TotalVisits))
        {
            TsDataRow(ws, row, lastCol, idx % 2 == 1);
            var active = user.TotalVisits - user.CompletedVisits;
            var pct = user.TotalVisits > 0 ? (double)user.CompletedVisits / user.TotalVisits * 100 : 0;

            ws.Cell(row, 1).Value = user.Username;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = user.TotalVisits;
            ws.Cell(row, 3).Value = user.CompletedVisits;
            ws.Cell(row, 4).Value = active;
            if (active > 0)
            {
                ws.Cell(row, 4).Style.Font.FontColor = TsOrange;
                ws.Cell(row, 4).Style.Font.Bold = true;
            }
            ws.Cell(row, 5).Value = FormatHoursExcel(user.TotalMinutes);
            ws.Cell(row, 6).Value = FormatHoursExcel(user.AverageMinutesPerVisit);
            ws.Cell(row, 7).Value = user.CustomerSummaries.Count;
            ws.Cell(row, 8).Value = $"{pct:F0}%";
            ws.Cell(row, 8).Style.Font.Bold = true;
            ws.Cell(row, 8).Style.Font.FontColor = pct >= 80 ? TsGreen : pct >= 50 ? TsOrange : TsRed;

            for (int c = 2; c <= lastCol; c++)
                ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++; idx++;
        }

        // Totals summary row
        TsSummaryRow(ws, row, lastCol);
        ws.Cell(row, 1).Value = $"TOTAL: {report.UserSummaries.Count} MERCHANDISERS";
        ws.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(row, 2).Value = report.TotalVisits;
        ws.Cell(row, 3).Value = totalCompleted;
        ws.Cell(row, 4).Value = report.TotalVisits - totalCompleted;
        ws.Cell(row, 5).Value = FormatHoursExcel(report.TotalHours * 60);
        ws.Cell(row, 6).Value = FormatHoursExcel(report.AverageVisitMinutes);
        ws.Cell(row, 7).Value = report.UserSummaries.SelectMany(u => u.CustomerSummaries).Select(c => c.CustomerCode).Distinct().Count();
        ws.Cell(row, 8).Value = $"{completionPct:F0}%";
        for (int c = 2; c <= lastCol; c++)
        {
            ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, c).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        row += 2;

        // \u2500\u2500 Daily Activity Table \u2500\u2500
        var dailyTotals = report.UserSummaries.SelectMany(u => u.DailySummaries)
            .GroupBy(d => d.Date)
            .Select(g => new
            {
                Date = g.Key,
                Visits = g.Sum(x => x.VisitCount),
                TotalMinutes = g.Sum(x => x.TotalMinutes),
                FirstCheckIn = g.Where(x => x.FirstCheckIn.HasValue).Min(x => x.FirstCheckIn),
                LastCheckOut = g.Where(x => x.LastCheckOut.HasValue).Max(x => x.LastCheckOut)
            })
            .OrderByDescending(d => d.Date).ToList();

        if (dailyTotals.Count > 0)
        {
            TsSectionTitle(ws, row, lastCol, "DAILY ACTIVITY");
            row += 2;

            string[] dayHeaders = ["Date", "Day", "Total Visits", "Total Time", "Avg per Visit", "First Check-In", "Last Check-Out", "Working Hours"];
            row = TsColumnHeaders(ws, row, lastCol, dayHeaders);

            idx = 0;
            foreach (var day in dailyTotals)
            {
                TsDataRow(ws, row, lastCol, idx % 2 == 1);
                ws.Cell(row, 1).Value = day.Date.ToString("dd MMM yyyy");
                ws.Cell(row, 2).Value = day.Date.ToString("ddd");
                if (day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    ws.Cell(row, 2).Style.Font.FontColor = TsOrange;
                ws.Cell(row, 3).Value = day.Visits;
                ws.Cell(row, 4).Value = FormatHoursExcel(day.TotalMinutes);
                ws.Cell(row, 5).Value = day.Visits > 0 ? FormatHoursExcel(day.TotalMinutes / day.Visits) : "\u2014";
                ws.Cell(row, 6).Value = day.FirstCheckIn.HasValue ? ToCatExcel(day.FirstCheckIn.Value).ToString("HH:mm") : "\u2014";
                ws.Cell(row, 7).Value = day.LastCheckOut.HasValue ? ToCatExcel(day.LastCheckOut.Value).ToString("HH:mm") : "Active";
                if (!day.LastCheckOut.HasValue)
                {
                    ws.Cell(row, 7).Style.Font.FontColor = TsOrange;
                    ws.Cell(row, 7).Style.Font.Bold = true;
                }
                if (day.FirstCheckIn.HasValue && day.LastCheckOut.HasValue)
                    ws.Cell(row, 8).Value = FormatHoursExcel((day.LastCheckOut.Value - day.FirstCheckIn.Value).TotalMinutes);
                else
                    ws.Cell(row, 8).Value = "\u2014";

                for (int c = 2; c <= lastCol; c++)
                    ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                row++; idx++;
            }
            row += 2;
        }

        // \u2500\u2500 Customer Summary \u2500\u2500
        var topCustomers = report.UserSummaries.SelectMany(u => u.CustomerSummaries)
            .GroupBy(c => new { c.CustomerCode, c.CustomerName })
            .Select(g => new { g.Key.CustomerCode, g.Key.CustomerName, Visits = g.Sum(x => x.VisitCount), TotalMinutes = g.Sum(x => x.TotalMinutes), Merchandisers = g.Count() })
            .OrderByDescending(c => c.Visits).ToList();

        if (topCustomers.Count > 0)
        {
            TsSectionTitle(ws, row, lastCol, "CUSTOMER SUMMARY");
            row += 2;

            row = TsColumnHeaders(ws, row, 6, ["Customer", "Code", "Total Visits", "Total Time", "Avg per Visit", "Merchandisers"]);

            idx = 0;
            foreach (var cust in topCustomers)
            {
                TsDataRow(ws, row, 6, idx % 2 == 1);
                ws.Cell(row, 1).Value = cust.CustomerName;
                ws.Cell(row, 2).Value = cust.CustomerCode;
                ws.Cell(row, 2).Style.Font.FontColor = TsTextMuted;
                ws.Cell(row, 3).Value = cust.Visits;
                ws.Cell(row, 4).Value = FormatHoursExcel(cust.TotalMinutes);
                ws.Cell(row, 5).Value = cust.Visits > 0 ? FormatHoursExcel(cust.TotalMinutes / cust.Visits) : "\u2014";
                ws.Cell(row, 6).Value = cust.Merchandisers;
                for (int c = 2; c <= 6; c++)
                    ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                row++; idx++;
            }
        }

        row += 2;
        TsDisclaimerRow(ws, row, lastCol, now);

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
        ws.Column(1).Width = 22;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Per-user detail sheet
    // ═══════════════════════════════════════════════════════════════
    private static void BuildTimesheetUserSheet(XLWorkbook workbook, TimesheetReportUserSummary user, DateTime? fromDate, DateTime? toDate, DateTime now)
    {
        const int lastCol = 7;
        var sheetName = user.Username.Length > 28 ? user.Username[..28] : user.Username;
        sheetName = string.Concat(sheetName.Select(c => ":\\/?*[]".Contains(c) ? '_' : c));
        var ws = workbook.Worksheets.Add(sheetName);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"TIMESHEET  \u2014  {user.Username.ToUpper()}", lastCol, now);

        // KPI strip
        var pct = user.TotalVisits > 0 ? (double)user.CompletedVisits / user.TotalVisits * 100 : 0;
        var pctColor = pct >= 80 ? TsGreen : pct >= 50 ? TsOrange : TsRed;

        row = TsKpiStrip(ws, row, lastCol,
            ("Total Visits", user.TotalVisits.ToString("N0"), null),
            ("Completed", user.CompletedVisits.ToString("N0"), null),
            ("Total Time", FormatHoursExcel(user.TotalMinutes), null),
            ("Avg per Visit", FormatHoursExcel(user.AverageMinutesPerVisit), null),
            ("Active Days", user.DailySummaries.Count.ToString("N0"), null),
            ("Shops Visited", user.CustomerSummaries.Count.ToString("N0"), null),
            ("Completion", $"{pct:F0}%", pctColor));

        // \u2500\u2500 Daily Breakdown \u2500\u2500
        TsSectionTitle(ws, row, lastCol, "DAILY BREAKDOWN");
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol, ["Date", "Day", "Visits", "Total Time", "Avg per Visit", "First Check-In", "Last Check-Out"]);

        int idx = 0;
        foreach (var day in user.DailySummaries.OrderByDescending(d => d.Date))
        {
            TsDataRow(ws, row, lastCol, idx % 2 == 1);
            ws.Cell(row, 1).Value = day.Date.ToString("dd MMM yyyy");
            ws.Cell(row, 2).Value = day.Date.ToString("ddd");
            if (day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                ws.Cell(row, 2).Style.Font.FontColor = TsOrange;
            ws.Cell(row, 3).Value = day.VisitCount;
            ws.Cell(row, 4).Value = FormatHoursExcel(day.TotalMinutes);
            ws.Cell(row, 5).Value = day.VisitCount > 0 ? FormatHoursExcel(day.TotalMinutes / day.VisitCount) : "\u2014";
            ws.Cell(row, 6).Value = day.FirstCheckIn.HasValue ? ToCatExcel(day.FirstCheckIn.Value).ToString("HH:mm") : "\u2014";
            ws.Cell(row, 7).Value = day.LastCheckOut.HasValue ? ToCatExcel(day.LastCheckOut.Value).ToString("HH:mm") : "Active";
            if (!day.LastCheckOut.HasValue)
            {
                ws.Cell(row, 7).Style.Font.FontColor = TsOrange;
                ws.Cell(row, 7).Style.Font.Bold = true;
            }
            for (int c = 2; c <= lastCol; c++)
                ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++; idx++;
        }

        // Daily totals
        TsSummaryRow(ws, row, lastCol);
        ws.Cell(row, 1).Value = $"TOTAL: {user.DailySummaries.Count} DAYS";
        ws.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(row, 3).Value = user.DailySummaries.Sum(d => d.VisitCount);
        ws.Cell(row, 4).Value = FormatHoursExcel(user.TotalMinutes);
        ws.Cell(row, 5).Value = FormatHoursExcel(user.AverageMinutesPerVisit);
        for (int c = 2; c <= lastCol; c++)
        {
            ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, c).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        row += 2;

        // \u2500\u2500 Customer Breakdown \u2500\u2500
        TsSectionTitle(ws, row, lastCol, "CUSTOMER BREAKDOWN");
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol, ["Customer", "Code", "Visits", "Total Time", "Avg per Visit", "% of Visits", "% of Time"]);

        idx = 0;
        foreach (var cust in user.CustomerSummaries.OrderByDescending(c => c.VisitCount))
        {
            TsDataRow(ws, row, lastCol, idx % 2 == 1);
            var visitPct = user.TotalVisits > 0 ? (double)cust.VisitCount / user.TotalVisits * 100 : 0;
            var timePct = user.TotalMinutes > 0 ? cust.TotalMinutes / user.TotalMinutes * 100 : 0;

            ws.Cell(row, 1).Value = cust.CustomerName;
            ws.Cell(row, 2).Value = cust.CustomerCode;
            ws.Cell(row, 2).Style.Font.FontColor = TsTextMuted;
            ws.Cell(row, 3).Value = cust.VisitCount;
            ws.Cell(row, 4).Value = FormatHoursExcel(cust.TotalMinutes);
            ws.Cell(row, 5).Value = cust.VisitCount > 0 ? FormatHoursExcel(cust.TotalMinutes / cust.VisitCount) : "\u2014";
            ws.Cell(row, 6).Value = $"{visitPct:F0}%";
            ws.Cell(row, 7).Value = $"{timePct:F0}%";

            for (int c = 2; c <= lastCol; c++)
                ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            if (idx == 0)
            {
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 6).Style.Font.Bold = true;
                ws.Cell(row, 6).Style.Font.FontColor = TsNavy;
            }
            row++; idx++;
        }

        // Customer totals
        TsSummaryRow(ws, row, lastCol);
        ws.Cell(row, 1).Value = $"TOTAL: {user.CustomerSummaries.Count} SHOPS";
        ws.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(row, 3).Value = user.TotalVisits;
        ws.Cell(row, 4).Value = FormatHoursExcel(user.TotalMinutes);
        ws.Cell(row, 5).Value = FormatHoursExcel(user.AverageMinutesPerVisit);
        ws.Cell(row, 6).Value = "100%";
        ws.Cell(row, 7).Value = "100%";
        for (int c = 2; c <= lastCol; c++)
        {
            ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, c).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        row += 2;

        TsDisclaimerRow(ws, row, lastCol, now);
        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
        ws.Column(1).Width = 30;
    }

    private static string FormatHoursExcel(double minutes)
    {
        var hours = (int)(minutes / 60);
        var mins = (int)(minutes % 60);
        return $"{hours}h {mins}m";
    }

    private static DateTime ToCatExcel(DateTime utc) => utc.AddHours(2);

    private static string FormatExcelDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return "";
        return DateTime.TryParse(dateStr, out var dt) ? dt.ToString("dd MMM yyyy") : dateStr;
    }

    private static byte[] WorkbookToBytes(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    // ─── Desktop Sales Export ─────────────────────────────────────

    public byte[] ExportDesktopSalesToExcel(List<DesktopSaleDto> sales, EndOfDayReportDto? report, DateTime? fromDate = null, DateTime? toDate = null)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Desktop Sales");
        const int cols = 9;

        var row = WriteReportHeader(ws, "Desktop Sales Report", cols, fromDate, toDate);

        // KPI cards
        if (report != null)
        {
            WriteKpiCard(ws, row, 1, "Total Sales", report.TotalSalesCount.ToString());
            WriteKpiCard(ws, row, 3, "Total Amount", report.TotalSalesAmount.ToString("N2"));
            WriteKpiCard(ws, row, 5, "Total VAT", report.TotalVatAmount.ToString("N2"));
            WriteKpiCard(ws, row, 7, "Posted", report.PostedInvoiceCount.ToString(), SuccessGreen);
            row += 3;
        }

        // Column headers
        var headers = new[] { "Reference", "Customer", "Card Code", "Warehouse", "Amount", "VAT", "Paid", "Fiscal Status", "Consolidation" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
            ws.Cell(row, i + 1).Style.Font.Bold = true;
            ws.Cell(row, i + 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(row, i + 1).Style.Fill.BackgroundColor = NavyBlue;
            ws.Cell(row, i + 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, i + 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }
        row++;

        // Data rows
        var isAlt = false;
        foreach (var sale in sales)
        {
            ws.Cell(row, 1).Value = sale.ExternalReferenceId;
            ws.Cell(row, 2).Value = sale.CardName ?? sale.CardCode;
            ws.Cell(row, 3).Value = sale.CardCode;
            ws.Cell(row, 4).Value = sale.WarehouseCode;
            ws.Cell(row, 5).Value = sale.TotalAmount;
            ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 6).Value = sale.VatAmount;
            ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 7).Value = sale.AmountPaid;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 8).Value = sale.FiscalizationStatus;
            ws.Cell(row, 9).Value = sale.ConsolidationStatus;

            if (isAlt)
            {
                ws.Range(row, 1, row, cols).Style.Fill.BackgroundColor = LightGray;
            }

            for (int c = 1; c <= cols; c++)
                ws.Cell(row, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

            isAlt = !isAlt;
            row++;
        }

        // Totals row
        ws.Cell(row, 1).Value = "TOTALS";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 5).Value = sales.Sum(s => s.TotalAmount);
        ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 5).Style.Font.Bold = true;
        ws.Cell(row, 6).Value = sales.Sum(s => s.VatAmount);
        ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 6).Style.Font.Bold = true;
        ws.Cell(row, 7).Value = sales.Sum(s => s.AmountPaid);
        ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 7).Style.Font.Bold = true;
        ws.Range(row, 1, row, cols).Style.Fill.BackgroundColor = TotalsBackground;

        ws.Columns().AdjustToContents();
        return WorkbookToBytes(workbook);
    }

    // ─── Local Stock Export ──────────────────────────────────────

    public byte[] ExportLocalStockToExcel(LocalStockResultDto stock)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Local Stock");
        const int cols = 7;

        var row = WriteReportHeader(ws, "Local Stock Snapshot", cols,
            subtitle: $"Warehouse: {stock.WarehouseCode}  |  Date: {stock.SnapshotDate:dd MMM yyyy}  |  Status: {stock.SnapshotStatus}");

        // KPI cards
        var inStock = stock.Items.Count(i => i.AvailableQuantity > 0);
        var outOfStock = stock.Items.Count(i => i.AvailableQuantity <= 0);
        var adjusted = stock.Items.Count(i => i.TransferAdjustment != 0);
        WriteKpiCard(ws, row, 1, "Total Items", stock.Items.Count.ToString());
        WriteKpiCard(ws, row, 3, "In Stock", inStock.ToString(), SuccessGreen);
        WriteKpiCard(ws, row, 5, "Out of Stock", outOfStock.ToString(), outOfStock > 0 ? DangerRed : SuccessGreen);
        WriteKpiCard(ws, row, 7, "Transfer Adjusted", adjusted.ToString());
        row += 3;

        // Column headers
        var headers = new[] { "Item Code", "Description", "Available Qty", "Original Qty", "Adjustment", "Batches", "Warehouse" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
            ws.Cell(row, i + 1).Style.Font.Bold = true;
            ws.Cell(row, i + 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(row, i + 1).Style.Fill.BackgroundColor = NavyBlue;
            ws.Cell(row, i + 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, i + 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }
        row++;

        // Data rows
        var isAlt = false;
        foreach (var item in stock.Items)
        {
            ws.Cell(row, 1).Value = item.ItemCode;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = item.ItemDescription ?? "";
            ws.Cell(row, 3).Value = item.AvailableQuantity;
            ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
            if (item.AvailableQuantity <= 0)
                ws.Cell(row, 3).Style.Font.FontColor = DangerRed;
            ws.Cell(row, 4).Value = item.OriginalQuantity;
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 5).Value = item.TransferAdjustment;
            ws.Cell(row, 5).Style.NumberFormat.Format = "+#,##0.00;-#,##0.00;0.00";
            if (item.TransferAdjustment > 0) ws.Cell(row, 5).Style.Font.FontColor = SuccessGreen;
            else if (item.TransferAdjustment < 0) ws.Cell(row, 5).Style.Font.FontColor = DangerRed;
            ws.Cell(row, 6).Value = item.Batches.Count;
            ws.Cell(row, 7).Value = item.WarehouseCode;

            if (isAlt)
                ws.Range(row, 1, row, cols).Style.Fill.BackgroundColor = LightGray;

            for (int c = 1; c <= cols; c++)
                ws.Cell(row, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

            isAlt = !isAlt;
            row++;

            // Batch detail rows
            if (item.Batches.Count > 1)
            {
                foreach (var batch in item.Batches.OrderBy(b => b.ExpiryDate))
                {
                    ws.Cell(row, 1).Value = "";
                    ws.Cell(row, 2).Value = $"  Batch: {batch.BatchNumber ?? "N/A"}" +
                        (batch.ExpiryDate.HasValue ? $" — Expires: {batch.ExpiryDate.Value:dd MMM yyyy}" : "");
                    ws.Cell(row, 2).Style.Font.FontSize = 9;
                    ws.Cell(row, 2).Style.Font.FontColor = XLColor.FromHtml("#616161");
                    ws.Cell(row, 3).Value = batch.AvailableQuantity;
                    ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(row, 3).Style.Font.FontSize = 9;
                    ws.Cell(row, 4).Value = batch.OriginalQuantity;
                    ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(row, 4).Style.Font.FontSize = 9;
                    ws.Range(row, 1, row, cols).Style.Fill.BackgroundColor = AccentBlue;
                    row++;
                }
            }
        }

        ws.Columns().AdjustToContents();
        return WorkbookToBytes(workbook);
    }
}