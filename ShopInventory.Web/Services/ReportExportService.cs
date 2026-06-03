using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ShopInventory.Web.Features.Reports.Queries.GetAccountSalesPaymentReport;
using ShopInventory.Web.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;
using ShopInventory.Web.Models;
using System.Text;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

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
    byte[] ExportAccountSalesPaymentReportToExcel(GetAccountSalesPaymentReportResult report);
    byte[] ExportMerchandiserPurchaseOrderReportToExcel(GetMerchandiserPurchaseOrderReportResult report);
    string GeneratePrintableHtml(string title, string content, DateTime? fromDate = null, DateTime? toDate = null);
}

public class ReportExportService : IReportExportService
{
    private const string CompanyName = "KEFALOS CHEESE (PVT) LTD";
    private const string SystemName = "Shop Inventory Management System";
    private const string BrandLogoRelativePath = "wwwroot/images/kefalos-logo.jpg";
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
    private static readonly XLColor ExecutiveIndigo = XLColor.FromHtml("#312E81");
    private static readonly XLColor ExecutiveRoyalBlue = XLColor.FromHtml("#2563EB");
    private static readonly XLColor ExecutiveCyan = XLColor.FromHtml("#06B6D4");
    private static readonly XLColor ExecutiveEmerald = XLColor.FromHtml("#10B981");
    private static readonly XLColor ExecutiveAmber = XLColor.FromHtml("#F59E0B");
    private static readonly XLColor ExecutiveRose = XLColor.FromHtml("#F43F5E");
    private static readonly XLColor ExecutiveCanvas = XLColor.FromHtml("#F8FAFC");
    private static readonly XLColor ExecutiveSurface = XLColor.FromHtml("#FFFFFF");
    private static readonly XLColor ExecutiveSection = XLColor.FromHtml("#EEF2FF");
    private static readonly XLColor ExecutiveTextPrimary = XLColor.FromHtml("#0F172A");
    private static readonly XLColor ExecutiveTextSecondary = XLColor.FromHtml("#475569");
    private static readonly XLColor ExecutiveTextMuted = XLColor.FromHtml("#94A3B8");
    private static readonly XLColor ExecutiveBorder = XLColor.FromHtml("#D9E2F2");
    private static readonly XLColor ExecutiveSoftBlue = XLColor.FromHtml("#DBEAFE");
    private static readonly XLColor ExecutiveSoftCyan = XLColor.FromHtml("#CFFAFE");
    private static readonly XLColor ExecutiveSoftEmerald = XLColor.FromHtml("#D1FAE5");
    private static readonly XLColor ExecutiveSoftAmber = XLColor.FromHtml("#FEF3C7");
    private static readonly XLColor ExecutiveSoftRose = XLColor.FromHtml("#FFE4E6");
    private static readonly XLColor ExecutiveSoftIndigo = XLColor.FromHtml("#E0E7FF");

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

    private static readonly XLColor PodNavy = XLColor.FromHtml("#1B3A5C");
    private static readonly XLColor PodHeaderBg = XLColor.FromHtml("#2C5F8A");
    private static readonly XLColor PodSubHeaderBg = XLColor.FromHtml("#E8EEF4");
    private static readonly XLColor PodStripeBg = XLColor.FromHtml("#F5F7FA");
    private static readonly XLColor PodGridColor = XLColor.FromHtml("#C5CED8");
    private static readonly XLColor PodGridLight = XLColor.FromHtml("#DDE3EA");
    private static readonly XLColor PodTextDark = XLColor.FromHtml("#1A1A2E");
    private static readonly XLColor PodTextMuted = XLColor.FromHtml("#5A6A7A");
    private static readonly XLColor PodTotalBg = XLColor.FromHtml("#DCE6F0");
    private static readonly XLColor PodTotalStripeBg = XLColor.FromHtml("#CCDBEB");
    private static readonly XLColor PodPendingBg = XLColor.FromHtml("#FFF3D6");
    private static readonly XLColor PodPendingStripeBg = XLColor.FromHtml("#FFE8B3");
    private static readonly XLColor PodUploadedBg = XLColor.FromHtml("#E8F3E8");
    private static readonly XLColor PodUploadedStripeBg = XLColor.FromHtml("#D8EBD8");
    private static readonly XLColor PodGreen = XLColor.FromHtml("#2E7D32");
    private static readonly XLColor PodOrange = XLColor.FromHtml("#E65100");
    private static readonly XLColor PodRed = XLColor.FromHtml("#C62828");
    private static readonly HashSet<string> PodExcelExcludedBusinessPartnerCodes = new(
        Enumerable.Range(1, 20).Select(number => $"VAN{number:000}")
            .Concat(Enumerable.Range(1, 7).Select(number => $"TEA{number:000}"))
            .Concat(Enumerable.Range(30, 7).Select(number => $"PRO{number:000}"))
            .Concat([
                "COR006",
                "COR007",
                "MAC006",
                "MAC009",
                "CHA009",
                "STE014",
                "ABI002",
                "LAN016",
                "RED002 FCA",
                "RED002(FCA)"
            ]),
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<int> PodExcelExcludedCreatorUserIds = [75, 51, 70, 1, 54, 32];

    private static void PodApplyDefaults(IXLWorksheet ws)
    {
        ws.Style.Font.FontName = "Aptos";
        ws.Style.Font.FontSize = 10;
    }

    private static int PodTitleBar(IXLWorksheet ws, string title, int lastCol, DateTime now)
    {
        ws.Row(1).Height = 32;
        var titleRange = ws.Range(1, 1, 1, lastCol);
        titleRange.Style.Fill.BackgroundColor = PodNavy;
        titleRange.Style.Font.FontColor = XLColor.White;
        titleRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        titleRange.Style.Border.BottomBorderColor = XLColor.FromHtml("#4A90C4");

        if (lastCol > 1)
            ws.Range(1, 1, 1, lastCol - 1).Merge();

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

    private static int PodKpiStrip(IXLWorksheet ws, int row, int lastCol, params (string Label, string Value, XLColor? Color)[] kpis)
    {
        if (kpis.Length == 0)
            return row;

        ws.Row(row).Height = 28;
        ws.Row(row + 1).Height = 18;

        for (int metricIndex = 0; metricIndex < kpis.Length; metricIndex++)
        {
            int startCol = (int)Math.Floor(metricIndex * lastCol / (double)kpis.Length) + 1;
            int endCol = (int)Math.Floor((metricIndex + 1) * lastCol / (double)kpis.Length);
            if (endCol < startCol)
                endCol = startCol;

            var valueRange = ws.Range(row, startCol, row, endCol);
            var labelRange = ws.Range(row + 1, startCol, row + 1, endCol);
            if (endCol > startCol)
            {
                valueRange.Merge();
                labelRange.Merge();
            }

            valueRange.Style.Fill.BackgroundColor = PodSubHeaderBg;
            valueRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            valueRange.Style.Border.OutsideBorderColor = PodGridColor;
            valueRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            valueRange.Style.Border.InsideBorderColor = PodGridLight;

            labelRange.Style.Fill.BackgroundColor = PodSubHeaderBg;
            labelRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            labelRange.Style.Border.OutsideBorderColor = PodGridColor;
            labelRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            labelRange.Style.Border.InsideBorderColor = PodGridLight;
            labelRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            labelRange.Style.Border.BottomBorderColor = PodGridColor;

            ws.Cell(row, startCol).Value = kpis[metricIndex].Value;
            ws.Cell(row, startCol).Style.Font.Bold = true;
            ws.Cell(row, startCol).Style.Font.FontSize = 14;
            ws.Cell(row, startCol).Style.Font.FontColor = kpis[metricIndex].Color ?? PodNavy;
            ws.Cell(row, startCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, startCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            ws.Cell(row + 1, startCol).Value = kpis[metricIndex].Label;
            ws.Cell(row + 1, startCol).Style.Font.FontSize = 8;
            ws.Cell(row + 1, startCol).Style.Font.FontColor = PodTextMuted;
            ws.Cell(row + 1, startCol).Style.Font.Italic = true;
            ws.Cell(row + 1, startCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row + 1, startCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        return row + 3;
    }

    private static void PodSectionTitle(IXLWorksheet ws, int row, int lastCol, string title)
    {
        ws.Range(row, 1, row, lastCol).Merge();
        var cell = ws.Cell(row, 1);
        cell.Value = title;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 11;
        cell.Style.Font.FontColor = PodNavy;
        cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.BottomBorderColor = PodGridColor;
    }

    private static int PodColumnHeaders(IXLWorksheet ws, int row, int lastCol, string[] headers)
    {
        ws.Row(row).Height = 38;
        var range = ws.Range(row, 1, row, lastCol);
        range.Style.Fill.BackgroundColor = PodHeaderBg;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 9;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = PodNavy;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#4A7DAA");

        for (int headerIndex = 0; headerIndex < headers.Length; headerIndex++)
            ws.Cell(row, headerIndex + 1).Value = headers[headerIndex];

        return row + 1;
    }

    private static void PodDataRow(IXLWorksheet ws, int row, int lastCol, bool isStripe)
    {
        var rowRange = ws.Range(row, 1, row, lastCol);
        rowRange.Style.Fill.BackgroundColor = isStripe ? PodStripeBg : XLColor.White;
        rowRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        rowRange.Style.Border.BottomBorderColor = PodGridLight;
        rowRange.Style.Font.FontSize = 10;
        rowRange.Style.Font.FontColor = PodTextDark;

        for (int columnIndex = 1; columnIndex <= lastCol; columnIndex++)
        {
            ws.Cell(row, columnIndex).Style.Border.LeftBorder = XLBorderStyleValues.Thin;
            ws.Cell(row, columnIndex).Style.Border.LeftBorderColor = PodGridLight;
            ws.Cell(row, columnIndex).Style.Border.RightBorder = XLBorderStyleValues.Thin;
            ws.Cell(row, columnIndex).Style.Border.RightBorderColor = PodGridLight;
        }

        ws.Cell(row, 1).Style.Border.LeftBorderColor = PodGridColor;
        ws.Cell(row, lastCol).Style.Border.RightBorderColor = PodGridColor;
    }

    private static void PodSummaryRow(IXLWorksheet ws, int row, int lastCol)
    {
        var range = ws.Range(row, 1, row, lastCol);
        range.Style.Fill.BackgroundColor = PodNavy;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 10;
        range.Style.Border.TopBorder = XLBorderStyleValues.Medium;
        range.Style.Border.TopBorderColor = PodNavy;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        range.Style.Border.OutsideBorderColor = PodNavy;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(row).Height = 26;
    }

    private static void PodDisclaimerRow(IXLWorksheet ws, int row, int lastCol, DateTime now)
    {
        ws.Range(row, 1, row, lastCol).Merge();
        var cell = ws.Cell(row, 1);
        cell.Value = $"This document was auto-generated by the Shop Inventory System on {now:dd MMM yyyy 'at' HH:mm} CAT. Data sourced from SAP Business One and POD upload records.";
        cell.Style.Font.FontSize = 8;
        cell.Style.Font.Italic = true;
        cell.Style.Font.FontColor = XLColor.FromHtml("#9CA3AF");
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void PodFinalize(IXLWorksheet ws, int lastCol, int freezeRow = 0, int freezeCol = 0)
    {
        ws.Columns(1, lastCol).AdjustToContents();
        for (int columnIndex = 1; columnIndex <= lastCol; columnIndex++)
        {
            if (ws.Column(columnIndex).Width > 42) ws.Column(columnIndex).Width = 42;
            if (ws.Column(columnIndex).Width < 11) ws.Column(columnIndex).Width = 11;
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

    private static string FormatPodReportPeriod(PodUploadStatusReport report)
    {
        var hasFromDate = DateTime.TryParse(report.FromDate, out var fromDate);
        var hasToDate = DateTime.TryParse(report.ToDate, out var toDate);

        if (hasFromDate && hasToDate)
            return $"{fromDate:dd MMM yyyy} to {toDate:dd MMM yyyy}";

        if (hasFromDate)
            return $"From {fromDate:dd MMM yyyy}";

        if (hasToDate)
            return $"To {toDate:dd MMM yyyy}";

        return "Selected period";
    }

    private static string FormatPodAmount(decimal amount) => amount.ToString("N2");

    private static string FormatPodUploadDate(DateTime? uploadedAt) =>
        uploadedAt.HasValue ? FormatCatDateTime(uploadedAt.Value) : "-";

    private static string FormatPodGeneratedLocationDisplay(PodUploadStatusItem item) =>
        string.IsNullOrWhiteSpace(item.CreatedLocation) ? "Unmapped creator" : item.CreatedLocation.Trim();

    private static bool IsPodExcelExcludedInvoice(PodUploadStatusItem item) =>
        IsPodExcelExcludedBusinessPartner(item)
        || IsPodExcelExcludedCreatorUser(item);

    private static bool IsPodExcelExcludedBusinessPartner(PodUploadStatusItem item) =>
        !string.IsNullOrWhiteSpace(item.CardCode)
        && PodExcelExcludedBusinessPartnerCodes.Contains(item.CardCode.Trim());

    private static bool IsPodExcelExcludedCreatorUser(PodUploadStatusItem item) =>
        item.CreatedByUserId.HasValue
        && PodExcelExcludedCreatorUserIds.Contains(item.CreatedByUserId.Value);

    private static int CalculatePodDaysAging(string? docDate, DateTime now)
    {
        if (!DateTime.TryParse(docDate, out var parsedDate))
            return 0;

        return Math.Max(0, (int)(now.Date - parsedDate.Date).TotalDays);
    }

    private static XLColor GetPodCompletionColor(double completionPct) => completionPct switch
    {
        >= 85 => PodGreen,
        >= 60 => PodOrange,
        _ => PodRed
    };

    private static void StylePodCurrencyCell(IXLCell cell, bool bold = false, XLColor? fontColor = null)
    {
        cell.Style.NumberFormat.Format = "#,##0.00";
        cell.Style.Font.Bold = bold;
        cell.Style.Font.FontColor = fontColor ?? PodTextDark;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    private static void StylePodTotalCell(IXLCell cell, bool isStripe)
    {
        StylePodCurrencyCell(cell, bold: true, fontColor: PodNavy);
        cell.Style.Fill.BackgroundColor = isStripe ? PodTotalStripeBg : PodTotalBg;
        cell.Style.Border.LeftBorder = XLBorderStyleValues.Medium;
        cell.Style.Border.LeftBorderColor = PodGridColor;
        cell.Style.Border.RightBorder = XLBorderStyleValues.Medium;
        cell.Style.Border.RightBorderColor = PodGridColor;
    }

    private static void StylePodStatusCell(IXLCell cell, bool hasPod, bool isStripe)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Fill.BackgroundColor = hasPod
            ? isStripe ? PodUploadedStripeBg : PodUploadedBg
            : isStripe ? PodPendingStripeBg : PodPendingBg;
        cell.Style.Font.FontColor = hasPod ? PodGreen : PodOrange;
    }

    private static void StylePodAgingCell(IXLCell cell, int daysAging)
    {
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Font.Bold = daysAging > 7;
        cell.Style.Font.FontColor = daysAging switch
        {
            > 14 => PodRed,
            > 7 => PodOrange,
            _ => PodGreen
        };
    }

    private static void StylePodMutedCell(IXLCell cell)
    {
        cell.Style.Font.FontColor = PodTextMuted;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    public byte[] ExportPodUploadStatusToExcel(PodUploadStatusReport report)
    {
        using var workbook = new XLWorkbook();
        var now = CurrentCatNow();
        var periodText = FormatPodReportPeriod(report);
        var reportItems = report.Items
            .Where(item => !IsPodExcelExcludedInvoice(item))
            .ToList();

        var totalInvoices = reportItems.Count;
        var uploadedCount = reportItems.Count(item => item.HasPod);
        var pendingCount = reportItems.Count(item => !item.HasPod);
        var totalAmount = reportItems.Sum(item => item.DocTotal);
        var uploadedAmount = reportItems.Where(item => item.HasPod).Sum(item => item.DocTotal);
        var pendingAmount = reportItems.Where(item => !item.HasPod).Sum(item => item.DocTotal);
        var completionPct = totalInvoices > 0
            ? uploadedCount / (double)totalInvoices * 100
            : 0;

        {
            var ws = workbook.Worksheets.Add("POD Dashboard");
            const int lastCol = 9;
            PodApplyDefaults(ws);

            var row = PodTitleBar(ws, $"POD UPLOAD STATUS - {periodText}", lastCol, now);
            row = PodKpiStrip(ws, row, lastCol,
                ("Total Invoices", totalInvoices.ToString("N0"), null),
                ("Uploaded", uploadedCount.ToString("N0"), PodGreen),
                ("Pending", pendingCount.ToString("N0"), pendingCount > 0 ? PodOrange : PodGreen),
                ("Completion", $"{completionPct:N1}%", GetPodCompletionColor(completionPct)),
                ("Total Value", FormatPodAmount(totalAmount), null),
                ("Pending Value", FormatPodAmount(pendingAmount), pendingAmount > 0 ? PodOrange : PodGreen));

            PodSectionTitle(ws, row, lastCol, "Uploaded vs pending POD status");
            row++;

            var headerRow = row;
            row = PodColumnHeaders(ws, row, lastCol,
            [
                "Invoice #",
                "Customer",
                "Card Code",
                "Invoice Date",
                "Generated Location",
                "Amount",
                "POD Status",
                "Uploaded",
                "TOTAL"
            ]);

            var rowIndex = 0;
            foreach (var item in reportItems)
            {
                var isStripe = rowIndex % 2 == 1;
                PodDataRow(ws, row, lastCol, isStripe);

                ws.Cell(row, 1).Value = item.DocNum;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 2).Value = item.CardName ?? "-";
                ws.Cell(row, 3).Value = item.CardCode ?? "-";
                ws.Cell(row, 4).Value = FormatExcelDate(item.DocDate);
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 5).Value = FormatPodGeneratedLocationDisplay(item);
                ws.Cell(row, 5).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 6).Value = item.DocTotal;
                StylePodCurrencyCell(ws.Cell(row, 6));

                ws.Cell(row, 7).Value = item.HasPod ? "Uploaded" : "Pending";
                StylePodStatusCell(ws.Cell(row, 7), item.HasPod, isStripe);

                if (item.HasPod && item.PodUploadedAt.HasValue)
                {
                    var uploadStr = FormatPodUploadDate(item.PodUploadedAt);
                    var uploaderDisplay = FormatPodUploadedByDisplay(item);
                    if (!string.IsNullOrEmpty(uploaderDisplay) && uploaderDisplay != "-")
                        uploadStr += $" ({uploaderDisplay})";
                    ws.Cell(row, 8).Value = uploadStr;
                    ws.Cell(row, 8).Style.Font.FontColor = PodTextMuted;
                }
                else
                {
                    ws.Cell(row, 8).Value = "-";
                    StylePodMutedCell(ws.Cell(row, 8));
                }

                ws.Cell(row, 9).Value = item.DocTotal;
                StylePodTotalCell(ws.Cell(row, 9), isStripe);

                row++;
                rowIndex++;
            }

            PodSummaryRow(ws, row, lastCol);
            ws.Cell(row, 1).Value = "SUMMARY";
            ws.Cell(row, 2).Value = $"{totalInvoices:N0} invoices";
            ws.Cell(row, 4).Value = periodText;
            ws.Cell(row, 6).Value = totalAmount;
            ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, 7).Value = $"{uploadedCount:N0} uploaded / {pendingCount:N0} pending";
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 9).Value = totalAmount;
            ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            PodDisclaimerRow(ws, row + 2, lastCol, now);
            PodFinalize(ws, lastCol, headerRow, 2);
            ws.Column(1).Width = 12;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 22;
            ws.Column(6).Width = 14;
            ws.Column(7).Width = 12;
            ws.Column(8).Width = 28;
            ws.Column(9).Width = 14;
        }

        var pending = reportItems.Where(item => !item.HasPod).OrderBy(item => item.DocDate).ToList();
        {
            var ws = workbook.Worksheets.Add("Pending PODs");
            const int lastCol = 7;
            PodApplyDefaults(ws);

            var oldestPendingDays = pending.Count > 0
                ? pending.Max(item => CalculatePodDaysAging(item.DocDate, now))
                : 0;
            var stalePendingCount = pending.Count(item => CalculatePodDaysAging(item.DocDate, now) > 14);

            var row = PodTitleBar(ws, $"PENDING POD UPLOADS - {periodText}", lastCol, now);
            row = PodKpiStrip(ws, row, lastCol,
                ("Pending Invoices", pending.Count.ToString("N0"), PodOrange),
                ("Pending Value", FormatPodAmount(pendingAmount), PodOrange),
                ("Oldest Age", $"{oldestPendingDays:N0} days", oldestPendingDays > 14 ? PodRed : PodOrange),
                ("Over 14 Days", stalePendingCount.ToString("N0"), stalePendingCount > 0 ? PodRed : PodGreen));

            PodSectionTitle(ws, row, lastCol, "Invoices awaiting POD upload");
            row++;

            var headerRow = row;
            row = PodColumnHeaders(ws, row, lastCol,
            [
                "Invoice #",
                "Customer",
                "Card Code",
                "Invoice Date",
                "Generated Location",
                "Days Aging",
                "TOTAL"
            ]);

            var rowIndex = 0;
            foreach (var item in pending)
            {
                var isStripe = rowIndex % 2 == 1;
                var daysAging = CalculatePodDaysAging(item.DocDate, now);
                PodDataRow(ws, row, lastCol, isStripe);

                ws.Cell(row, 1).Value = item.DocNum;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 2).Value = item.CardName ?? "-";
                ws.Cell(row, 3).Value = item.CardCode ?? "-";
                ws.Cell(row, 4).Value = FormatExcelDate(item.DocDate);
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 5).Value = FormatPodGeneratedLocationDisplay(item);
                ws.Cell(row, 5).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 6).Value = daysAging;
                StylePodAgingCell(ws.Cell(row, 6), daysAging);
                ws.Cell(row, 7).Value = item.DocTotal;
                StylePodTotalCell(ws.Cell(row, 7), isStripe);

                row++;
                rowIndex++;
            }

            PodSummaryRow(ws, row, lastCol);
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 2).Value = $"{pending.Count:N0} invoices";
            ws.Cell(row, 6).Value = $"Oldest: {oldestPendingDays:N0} days";
            ws.Cell(row, 7).Value = pendingAmount;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            PodDisclaimerRow(ws, row + 2, lastCol, now);
            PodFinalize(ws, lastCol, headerRow, 2);
            ws.Column(1).Width = 12;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 22;
            ws.Column(6).Width = 12;
            ws.Column(7).Width = 14;
        }

        var uploadsByUser = reportItems
            .Where(item => item.HasPod)
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
            const int lastCol = 5;
            PodApplyDefaults(ws);

            var row = PodTitleBar(ws, $"POD UPLOADS BY USER - {periodText}", lastCol, now);
            row = PodKpiStrip(ws, row, lastCol,
                ("Uploaders", uploadsByUser.Count.ToString("N0"), null),
                ("Invoice Coverage", uploadsByUser.Sum(group => group.UploadedInvoices).ToString("N0"), PodGreen),
                ("POD Files", uploadsByUser.Sum(group => group.TotalFiles).ToString("N0"), PodGreen),
                ("Uploaded Value", FormatPodAmount(uploadsByUser.Sum(group => group.TotalAmount)), null));

            PodSectionTitle(ws, row, lastCol, "Uploader performance");
            row++;

            var headerRow = row;
            row = PodColumnHeaders(ws, row, lastCol,
            [
                "Uploaded By",
                "Invoices Covered",
                "POD Files",
                "Invoice Amount",
                "Latest Upload"
            ]);

            var rowIndex = 0;
            foreach (var group in uploadsByUser)
            {
                var isStripe = rowIndex % 2 == 1;
                PodDataRow(ws, row, lastCol, isStripe);

                ws.Cell(row, 1).Value = group.UploadedBy;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = group.UploadedInvoices;
                ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 3).Value = group.TotalFiles;
                ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 4).Value = group.TotalAmount;
                StylePodCurrencyCell(ws.Cell(row, 4));
                ws.Cell(row, 5).Value = FormatPodUploadDate(group.LatestUpload);
                if (!group.LatestUpload.HasValue)
                    StylePodMutedCell(ws.Cell(row, 5));

                row++;
                rowIndex++;
            }

            PodSummaryRow(ws, row, lastCol);
            ws.Cell(row, 1).Value = "SUMMARY";
            ws.Cell(row, 2).Value = uploadsByUser.Sum(group => group.UploadedInvoices);
            ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 3).Value = uploadsByUser.Sum(group => group.TotalFiles);
            ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 4).Value = uploadsByUser.Sum(group => group.TotalAmount);
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, 5).Value = uploadsByUser.Count == 1
                ? uploadsByUser[0].UploadedBy
                : $"{uploadsByUser.Count:N0} uploaders";

            PodDisclaimerRow(ws, row + 2, lastCol, now);
            PodFinalize(ws, lastCol, headerRow, 1);
            ws.Column(1).Width = 28;
            ws.Column(2).Width = 16;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 16;
            ws.Column(5).Width = 22;
        }

        var uploaded = reportItems.Where(item => item.HasPod).OrderByDescending(item => item.PodUploadedAt).ToList();
        {
            var ws = workbook.Worksheets.Add("Uploaded PODs");
            const int lastCol = 8;
            PodApplyDefaults(ws);

            var uploadedFileCount = uploaded.Sum(item => item.PodCount > 0
                ? item.PodCount
                : GetPodUploadedByUsers(item).Sum(user => user.FileCount));
            var uploadedUsers = uploaded
                .SelectMany(GetPodUploadedByUsers)
                .Select(user => user.Username.Trim())
                .Where(username => !string.IsNullOrWhiteSpace(username))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var latestUpload = uploaded.Count > 0
                ? uploaded.Max(item => item.PodUploadedAt)
                : null;

            var row = PodTitleBar(ws, $"INVOICES WITH POD UPLOADED - {periodText}", lastCol, now);
            row = PodKpiStrip(ws, row, lastCol,
                ("Uploaded Invoices", uploaded.Count.ToString("N0"), PodGreen),
                ("Uploaded Value", FormatPodAmount(uploadedAmount), null),
                ("POD Files", uploadedFileCount.ToString("N0"), PodGreen),
                ("Uploaders", uploadedUsers.ToString("N0"), null),
                ("Latest Upload", FormatPodUploadDate(latestUpload), PodTextMuted));

            PodSectionTitle(ws, row, lastCol, "Invoices with uploaded PODs");
            row++;

            var headerRow = row;
            row = PodColumnHeaders(ws, row, lastCol,
            [
                "Invoice #",
                "Customer",
                "Card Code",
                "Invoice Date",
                "Generated Location",
                "Uploaded",
                "Uploaded By",
                "TOTAL"
            ]);

            var rowIndex = 0;
            foreach (var item in uploaded)
            {
                var isStripe = rowIndex % 2 == 1;
                PodDataRow(ws, row, lastCol, isStripe);

                ws.Cell(row, 1).Value = item.DocNum;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 2).Value = item.CardName ?? "-";
                ws.Cell(row, 3).Value = item.CardCode ?? "-";
                ws.Cell(row, 4).Value = FormatExcelDate(item.DocDate);
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 5).Value = FormatPodGeneratedLocationDisplay(item);
                ws.Cell(row, 5).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 6).Value = FormatPodUploadDate(item.PodUploadedAt);
                ws.Cell(row, 6).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 7).Value = FormatPodUploadedByDisplay(item);
                ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 8).Value = item.DocTotal;
                StylePodTotalCell(ws.Cell(row, 8), isStripe);

                row++;
                rowIndex++;
            }

            PodSummaryRow(ws, row, lastCol);
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 2).Value = $"{uploaded.Count:N0} invoices";
            ws.Cell(row, 6).Value = $"{uploadedFileCount:N0} files";
            ws.Cell(row, 7).Value = $"{uploadedUsers:N0} uploaders";
            ws.Cell(row, 8).Value = uploadedAmount;
            ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            PodDisclaimerRow(ws, row + 2, lastCol, now);
            PodFinalize(ws, lastCol, headerRow, 2);
            ws.Column(1).Width = 12;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 14;
            ws.Column(5).Width = 22;
            ws.Column(6).Width = 22;
            ws.Column(7).Width = 14;
            ws.Column(8).Width = 14;
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

    public byte[] ExportAccountSalesPaymentReportToExcel(GetAccountSalesPaymentReportResult report)
    {
        using var workbook = new XLWorkbook();

        workbook.Style.Font.FontName = "Segoe UI";
        workbook.Style.Font.FontColor = ExecutiveTextPrimary;

        var logoPath = ResolveExecutiveLogoPath();

        var totalOutstandingUsd = report.Summary.TotalSalesUsd - report.Summary.TotalIncomingPaymentsUsd;
        var totalOutstandingZig = report.Summary.TotalSalesZig - report.Summary.TotalIncomingPaymentsZig;
        var totalTransactions = report.Summary.TotalInvoices + report.Summary.TotalPayments;
        var averageTransactionUsd = report.Summary.TotalInvoices > 0
            ? report.Summary.TotalSalesUsd / report.Summary.TotalInvoices
            : 0m;
        var averageTransactionZig = report.Summary.TotalInvoices > 0
            ? report.Summary.TotalSalesZig / report.Summary.TotalInvoices
            : 0m;

        var orderedAccounts = report.AccountTotals
            .OrderByDescending(account => account.TotalSalesUsd + account.TotalSalesZig + account.IncomingPaymentsUsd + account.IncomingPaymentsZig)
            .ThenBy(account => account.CardCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var orderedPeriods = report.Periods
            .OrderBy(period => period.PeriodStartUtc)
            .ToList();

        var topExposure = orderedAccounts
            .Select(account => new
            {
                account.CardCode,
                account.CardName,
                OutstandingUsd = account.TotalSalesUsd - account.IncomingPaymentsUsd,
                OutstandingZig = account.TotalSalesZig - account.IncomingPaymentsZig
            })
            .OrderByDescending(account => Math.Max(Math.Abs(account.OutstandingUsd), Math.Abs(account.OutstandingZig)))
            .FirstOrDefault();

        var periodSalesUsdMax = Math.Max(1m, orderedPeriods.Any() ? orderedPeriods.Max(period => period.TotalSalesUsd) : 0m);
        var periodPaymentsUsdMax = Math.Max(1m, orderedPeriods.Any() ? orderedPeriods.Max(period => period.IncomingPaymentsUsd) : 0m);
        var accountSalesUsdMax = Math.Max(1m, orderedAccounts.Any() ? orderedAccounts.Max(account => account.TotalSalesUsd) : 0m);

        var distinctInvoiceTotals = report.InvoiceDetails
            .GroupBy(invoice => $"{invoice.Source}|{invoice.DocumentEntry}|{invoice.DocumentNumber}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Max(invoice => invoice.DocumentTotal))
            .ToList();
        var invoiceHighValueThreshold = CalculateExecutiveHighValueThreshold(distinctInvoiceTotals);

        var paymentHighValueThreshold = CalculateExecutiveHighValueThreshold(report.PaymentDetails.Select(payment => payment.TotalAmount));

        var dashboard = workbook.Worksheets.Add("Dashboard");
        ConfigureExecutiveSheet(dashboard, 15, ExecutiveIndigo);
        int row = WriteExecutiveBanner(
            dashboard,
            "Sales and Incoming Payments",
            "Executive accounting workbook for reconciliation, collections, and customer-level transaction analysis.",
            report,
            15);
        TryAddExecutiveLogo(dashboard, logoPath, 2, 13, 0.14);

        WriteExecutiveKpiCard(dashboard, row, 1, 3, ExecutiveRoyalBlue, "TOTAL SALES", FormatExecutiveMoneyPair(report.Summary.TotalSalesUsd, report.Summary.TotalSalesZig), "Gross invoiced value across the selected accounts.");
        WriteExecutiveKpiCard(dashboard, row, 4, 6, ExecutiveEmerald, "TOTAL PAYMENTS", FormatExecutiveMoneyPair(report.Summary.TotalIncomingPaymentsUsd, report.Summary.TotalIncomingPaymentsZig), "Cash collections grouped by payment date.");
        WriteExecutiveKpiCard(dashboard, row, 7, 9, ExecutiveAmber, "OUTSTANDING BALANCE", FormatExecutiveMoneyPair(totalOutstandingUsd, totalOutstandingZig), "Open exposure after collections are applied.");
        WriteExecutiveKpiCard(dashboard, row, 10, 12, ExecutiveCyan, "TRANSACTIONS", totalTransactions.ToString("N0"), $"Invoices {report.Summary.TotalInvoices:N0}  |  Payments {report.Summary.TotalPayments:N0}");
        WriteExecutiveKpiCard(dashboard, row, 13, 15, ExecutiveRose, "AVG TRANSACTION", FormatExecutiveMoneyPair(averageTransactionUsd, averageTransactionZig), "Average invoice value for the selected range.");
        row += 7;

        WriteExecutiveCallout(
            dashboard,
            row,
            15,
            "EXECUTIVE SUMMARY",
            $"Active accounts: {report.Summary.ActiveAccountCount:N0} of {report.Summary.RequestedAccountCount:N0}. " +
            $"Collection performance sits at USD {report.Summary.CollectionRatePercentUsd:N2}% and ZiG {report.Summary.CollectionRatePercentZig:N2}%. " +
            (topExposure is null
                ? "No customer exposure was returned for this report."
                : $"Highest exposure currently sits with {topExposure.CardCode} {topExposure.CardName} at {FormatExecutiveMoneyPair(topExposure.OutstandingUsd, topExposure.OutstandingZig)}."));
        row += 4;

        WriteExecutiveSectionHeader(
            dashboard,
            row,
            15,
            "TREND SNAPSHOT",
            "A quick scan of grouped sales, collections, and outstanding balances over the selected reporting periods.",
            ExecutiveCyan);
        row += 2;

        var trendHeaderRow = row;
        var trendHeaders = new[] { "Period", "Invoices", "Payments", "Sales USD", "Payments USD", "Outstanding USD", "Collection %", "Sales Pulse", "Sales ZiG", "Payments ZiG", "Outstanding ZiG", "ZiG Pulse" };
        for (var index = 0; index < trendHeaders.Length; index++)
        {
            dashboard.Cell(trendHeaderRow, index + 1).Value = trendHeaders[index];
        }
        StyleExecutiveTableHeader(dashboard, trendHeaderRow, trendHeaders.Length, ExecutiveIndigo);
        row++;

        var previewPeriods = orderedPeriods.TakeLast(8).ToList();
        if (previewPeriods.Any())
        {
            var dataStart = row;
            foreach (var period in previewPeriods)
            {
                var outstandingUsd = period.TotalSalesUsd - period.IncomingPaymentsUsd;
                var outstandingZig = period.TotalSalesZig - period.IncomingPaymentsZig;

                dashboard.Cell(row, 1).Value = period.Label;
                dashboard.Cell(row, 2).Value = period.InvoiceCount;
                dashboard.Cell(row, 3).Value = period.PaymentCount;
                dashboard.Cell(row, 4).Value = period.TotalSalesUsd;
                dashboard.Cell(row, 5).Value = period.IncomingPaymentsUsd;
                dashboard.Cell(row, 6).Value = outstandingUsd;
                dashboard.Cell(row, 7).Value = FormatExecutivePercent(CalculateExecutivePercent(period.IncomingPaymentsUsd, period.TotalSalesUsd));
                dashboard.Cell(row, 8).Value = BuildExecutiveSignalBar(period.TotalSalesUsd, periodSalesUsdMax);
                dashboard.Cell(row, 9).Value = period.TotalSalesZig;
                dashboard.Cell(row, 10).Value = period.IncomingPaymentsZig;
                dashboard.Cell(row, 11).Value = outstandingZig;
                dashboard.Cell(row, 12).Value = BuildExecutiveSignalBar(period.IncomingPaymentsUsd, periodPaymentsUsdMax);

                dashboard.Range(row, 4, row, 6).Style.NumberFormat.Format = "#,##0.00";
                dashboard.Range(row, 9, row, 11).Style.NumberFormat.Format = "#,##0.00";
                dashboard.Cell(row, 8).Style.Font.FontName = "Consolas";
                dashboard.Cell(row, 12).Style.Font.FontName = "Consolas";
                ApplyExecutiveOutstandingStyle(dashboard.Cell(row, 6), outstandingUsd);
                ApplyExecutiveOutstandingStyle(dashboard.Cell(row, 11), outstandingZig);
                row++;
            }

            StyleExecutiveTableRows(dashboard, dataStart, row - 1, trendHeaders.Length);
        }
        else
        {
            dashboard.Range(row, 1, row, trendHeaders.Length).Merge();
            dashboard.Cell(row, 1).Value = "No grouped periods were returned for this report.";
            dashboard.Cell(row, 1).Style.Font.Italic = true;
            dashboard.Cell(row, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            row++;
        }

        row += 2;

        WriteExecutiveSectionHeader(
            dashboard,
            row,
            15,
            "CUSTOMER CONTRIBUTION",
            "Top accounts ranked by invoiced value, outstanding exposure, and collection quality.",
            ExecutiveRose);
        row += 2;

        var accountHeaderRow = row;
        var accountHeaders = new[] { "Card Code", "Card Name", "Sales USD", "Payments USD", "Outstanding USD", "Share USD %", "Sales ZiG", "Payments ZiG", "Outstanding ZiG", "Share ZiG %", "Pulse", "Status" };
        for (var index = 0; index < accountHeaders.Length; index++)
        {
            dashboard.Cell(accountHeaderRow, index + 1).Value = accountHeaders[index];
        }
        StyleExecutiveTableHeader(dashboard, accountHeaderRow, accountHeaders.Length, ExecutiveIndigo);
        row++;

        var previewAccounts = orderedAccounts.Take(8).ToList();
        if (previewAccounts.Any())
        {
            var dataStart = row;
            foreach (var account in previewAccounts)
            {
                var outstandingUsd = account.TotalSalesUsd - account.IncomingPaymentsUsd;
                var outstandingZig = account.TotalSalesZig - account.IncomingPaymentsZig;
                var usdShare = CalculateExecutivePercent(account.TotalSalesUsd, report.Summary.TotalSalesUsd);
                var zigShare = CalculateExecutivePercent(account.TotalSalesZig, report.Summary.TotalSalesZig);
                var pulseRatio = Math.Max(
                    report.Summary.TotalSalesUsd > 0 ? account.TotalSalesUsd / report.Summary.TotalSalesUsd : 0m,
                    report.Summary.TotalSalesZig > 0 ? account.TotalSalesZig / report.Summary.TotalSalesZig : 0m);

                dashboard.Cell(row, 1).Value = account.CardCode;
                dashboard.Cell(row, 2).Value = account.CardName;
                dashboard.Cell(row, 3).Value = account.TotalSalesUsd;
                dashboard.Cell(row, 4).Value = account.IncomingPaymentsUsd;
                dashboard.Cell(row, 5).Value = outstandingUsd;
                dashboard.Cell(row, 6).Value = FormatExecutivePercent(usdShare);
                dashboard.Cell(row, 7).Value = account.TotalSalesZig;
                dashboard.Cell(row, 8).Value = account.IncomingPaymentsZig;
                dashboard.Cell(row, 9).Value = outstandingZig;
                dashboard.Cell(row, 10).Value = FormatExecutivePercent(zigShare);
                dashboard.Cell(row, 11).Value = BuildExecutiveSignalBar(pulseRatio, 1m);
                dashboard.Cell(row, 11).Style.Font.FontName = "Consolas";
                dashboard.Cell(row, 12).Value = ResolveExecutiveCollectionStatus(outstandingUsd, outstandingZig, account.CollectionRatePercentUsd, account.CollectionRatePercentZig);

                dashboard.Range(row, 3, row, 5).Style.NumberFormat.Format = "#,##0.00";
                dashboard.Range(row, 7, row, 9).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveOutstandingStyle(dashboard.Cell(row, 5), outstandingUsd);
                ApplyExecutiveOutstandingStyle(dashboard.Cell(row, 9), outstandingZig);
                ApplyExecutiveStatusBadge(dashboard.Cell(row, 12));
                row++;
            }

            StyleExecutiveTableRows(dashboard, dataStart, row - 1, accountHeaders.Length);
        }
        else
        {
            dashboard.Range(row, 1, row, accountHeaders.Length).Merge();
            dashboard.Cell(row, 1).Value = "No customer contribution rows were returned for this report.";
            dashboard.Cell(row, 1).Style.Font.Italic = true;
            dashboard.Cell(row, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            row++;
        }

        WriteExecutiveFooter(dashboard, row + 1, 15);
        FinalizeExecutiveSheet(dashboard, 15, landscape: true);

        var visualsSheet = workbook.Worksheets.Add("Visuals");
        ConfigureExecutiveSheet(visualsSheet, 14, ExecutiveIndigo);
        var visualsRow = WriteExecutiveBannerSimple(
            visualsSheet,
            "Executive Visuals",
            "Native Excel charts for management packs, review meetings, and accounting narration.",
            report,
            14,
            ExecutiveIndigo);
        WriteExecutiveCallout(
            visualsSheet,
            visualsRow,
            14,
            "NATIVE CHART OBJECTS",
            "These visuals are embedded as real Excel charts, so finance and operations can resize, move, or reuse them directly in presentation packs without rebuilding the workbook.");
        visualsRow += 5;
        WriteExecutiveChartContainer(
            visualsSheet,
            visualsRow,
            1,
            visualsRow + 16,
            7,
            "PERIOD SALES VS COLLECTIONS",
            "Clustered USD chart sourced from the Trend Analysis sheet.",
            ExecutiveRoyalBlue);
        WriteExecutiveChartContainer(
            visualsSheet,
            visualsRow,
            8,
            visualsRow + 16,
            14,
            "TOP ACCOUNTS: SALES VS OUTSTANDING",
            "Clustered USD chart sourced from the Customer Analysis sheet.",
            ExecutiveRose);
        WriteExecutiveFooter(visualsSheet, visualsRow + 18, 14);
        FinalizeExecutiveSheet(visualsSheet, 14, landscape: true);

        var trendSheet = workbook.Worksheets.Add("Trend Analysis");
        ConfigureExecutiveSheet(trendSheet, 14, ExecutiveRoyalBlue);
        var trendRow = WriteExecutiveBannerSimple(
            trendSheet,
            "Trend Analysis",
            "Sales, collections, and outstanding balances by reporting bucket.",
            report,
            14,
            ExecutiveRoyalBlue);

        var trendDetailHeaders = new[] { "Period", "Start (CAT)", "End (CAT)", "Accounts", "Invoices", "Payments", "Sales USD", "Payments USD", "Outstanding USD", "Collection USD %", "USD Pulse", "Sales ZiG", "Payments ZiG", "Outstanding ZiG" };
        for (var index = 0; index < trendDetailHeaders.Length; index++)
        {
            trendSheet.Cell(trendRow, index + 1).Value = trendDetailHeaders[index];
        }
        StyleExecutiveTableHeader(trendSheet, trendRow, trendDetailHeaders.Length, ExecutiveRoyalBlue);
        var trendFreeze = trendRow;
        trendRow++;

        if (orderedPeriods.Any())
        {
            var dataStart = trendRow;
            foreach (var period in orderedPeriods)
            {
                var outstandingUsd = period.TotalSalesUsd - period.IncomingPaymentsUsd;
                var outstandingZig = period.TotalSalesZig - period.IncomingPaymentsZig;

                trendSheet.Cell(trendRow, 1).Value = period.Label;
                trendSheet.Cell(trendRow, 2).Value = FormatCatDate(period.PeriodStartUtc);
                trendSheet.Cell(trendRow, 3).Value = FormatCatDate(period.PeriodEndUtc);
                trendSheet.Cell(trendRow, 4).Value = period.Accounts.Count;
                trendSheet.Cell(trendRow, 5).Value = period.InvoiceCount;
                trendSheet.Cell(trendRow, 6).Value = period.PaymentCount;
                trendSheet.Cell(trendRow, 7).Value = period.TotalSalesUsd;
                trendSheet.Cell(trendRow, 8).Value = period.IncomingPaymentsUsd;
                trendSheet.Cell(trendRow, 9).Value = outstandingUsd;
                trendSheet.Cell(trendRow, 10).Value = FormatExecutivePercent(CalculateExecutivePercent(period.IncomingPaymentsUsd, period.TotalSalesUsd));
                trendSheet.Cell(trendRow, 11).Value = BuildExecutiveSignalBar(period.TotalSalesUsd, periodSalesUsdMax);
                trendSheet.Cell(trendRow, 12).Value = period.TotalSalesZig;
                trendSheet.Cell(trendRow, 13).Value = period.IncomingPaymentsZig;
                trendSheet.Cell(trendRow, 14).Value = outstandingZig;

                trendSheet.Range(trendRow, 7, trendRow, 9).Style.NumberFormat.Format = "#,##0.00";
                trendSheet.Range(trendRow, 12, trendRow, 14).Style.NumberFormat.Format = "#,##0.00";
                trendSheet.Cell(trendRow, 11).Style.Font.FontName = "Consolas";
                ApplyExecutiveOutstandingStyle(trendSheet.Cell(trendRow, 9), outstandingUsd);
                ApplyExecutiveOutstandingStyle(trendSheet.Cell(trendRow, 14), outstandingZig);
                trendRow++;
            }

            StyleExecutiveTableRows(trendSheet, dataStart, trendRow - 1, trendDetailHeaders.Length);
        }
        else
        {
            trendSheet.Range(trendRow, 1, trendRow, trendDetailHeaders.Length).Merge();
            trendSheet.Cell(trendRow, 1).Value = "No trend data is available for the selected filters.";
            trendSheet.Cell(trendRow, 1).Style.Font.Italic = true;
            trendSheet.Cell(trendRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            trendRow++;
        }

        var trendDataStartRow = trendFreeze + 1;
        var trendDataEndRow = orderedPeriods.Any() ? trendRow - 1 : trendDataStartRow - 1;

        WriteExecutiveFooter(trendSheet, trendRow + 1, 14);
        FinalizeExecutiveSheet(trendSheet, 14, trendFreeze, landscape: true);

        var accountSheet = workbook.Worksheets.Add("Customer Analysis");
        ConfigureExecutiveSheet(accountSheet, 14, ExecutiveCyan);
        var accountRow = WriteExecutiveBannerSimple(
            accountSheet,
            "Customer Analysis",
            "Contribution, outstanding balances, and settlement posture by requested account.",
            report,
            14,
            ExecutiveCyan);

        var accountDetailHeaders = new[] { "Card Code", "Card Name", "Invoices", "Payments", "Sales USD", "Collections USD", "Outstanding USD", "Share USD %", "Sales ZiG", "Collections ZiG", "Outstanding ZiG", "Share ZiG %", "Pulse", "Status" };
        for (var index = 0; index < accountDetailHeaders.Length; index++)
        {
            accountSheet.Cell(accountRow, index + 1).Value = accountDetailHeaders[index];
        }
        StyleExecutiveTableHeader(accountSheet, accountRow, accountDetailHeaders.Length, ExecutiveCyan);
        var accountFreeze = accountRow;
        accountRow++;

        if (orderedAccounts.Any())
        {
            var dataStart = accountRow;
            foreach (var account in orderedAccounts)
            {
                var outstandingUsd = account.TotalSalesUsd - account.IncomingPaymentsUsd;
                var outstandingZig = account.TotalSalesZig - account.IncomingPaymentsZig;
                var usdShare = CalculateExecutivePercent(account.TotalSalesUsd, report.Summary.TotalSalesUsd);
                var zigShare = CalculateExecutivePercent(account.TotalSalesZig, report.Summary.TotalSalesZig);

                accountSheet.Cell(accountRow, 1).Value = account.CardCode;
                accountSheet.Cell(accountRow, 2).Value = account.CardName;
                accountSheet.Cell(accountRow, 3).Value = account.InvoiceCount;
                accountSheet.Cell(accountRow, 4).Value = account.PaymentCount;
                accountSheet.Cell(accountRow, 5).Value = account.TotalSalesUsd;
                accountSheet.Cell(accountRow, 6).Value = account.IncomingPaymentsUsd;
                accountSheet.Cell(accountRow, 7).Value = outstandingUsd;
                accountSheet.Cell(accountRow, 8).Value = FormatExecutivePercent(usdShare);
                accountSheet.Cell(accountRow, 9).Value = account.TotalSalesZig;
                accountSheet.Cell(accountRow, 10).Value = account.IncomingPaymentsZig;
                accountSheet.Cell(accountRow, 11).Value = outstandingZig;
                accountSheet.Cell(accountRow, 12).Value = FormatExecutivePercent(zigShare);
                accountSheet.Cell(accountRow, 13).Value = BuildExecutiveSignalBar(account.TotalSalesUsd, accountSalesUsdMax);
                accountSheet.Cell(accountRow, 13).Style.Font.FontName = "Consolas";
                accountSheet.Cell(accountRow, 14).Value = ResolveExecutiveCollectionStatus(outstandingUsd, outstandingZig, account.CollectionRatePercentUsd, account.CollectionRatePercentZig);

                accountSheet.Range(accountRow, 5, accountRow, 7).Style.NumberFormat.Format = "#,##0.00";
                accountSheet.Range(accountRow, 9, accountRow, 11).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveOutstandingStyle(accountSheet.Cell(accountRow, 7), outstandingUsd);
                ApplyExecutiveOutstandingStyle(accountSheet.Cell(accountRow, 11), outstandingZig);
                ApplyExecutiveStatusBadge(accountSheet.Cell(accountRow, 14));
                accountRow++;
            }

            StyleExecutiveTableRows(accountSheet, dataStart, accountRow - 1, accountDetailHeaders.Length);
        }
        else
        {
            accountSheet.Range(accountRow, 1, accountRow, accountDetailHeaders.Length).Merge();
            accountSheet.Cell(accountRow, 1).Value = "No customer analysis rows are available for this report.";
            accountSheet.Cell(accountRow, 1).Style.Font.Italic = true;
            accountSheet.Cell(accountRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            accountRow++;
        }

        var accountDataStartRow = accountFreeze + 1;
        var accountDataEndRow = orderedAccounts.Any() ? accountRow - 1 : accountDataStartRow - 1;
        var accountChartDataEndRow = accountDataEndRow >= accountDataStartRow
            ? Math.Min(accountDataStartRow + 7, accountDataEndRow)
            : accountDataStartRow - 1;

        WriteExecutiveFooter(accountSheet, accountRow + 1, 14);
        FinalizeExecutiveSheet(accountSheet, 14, accountFreeze, landscape: true);

        var itemSheet = workbook.Worksheets.Add("Item Summary");
        ConfigureExecutiveSheet(itemSheet, 9, ExecutiveEmerald);
        var itemRow = WriteExecutiveBannerSimple(
            itemSheet,
            "Item Summary",
            "Item-level rollup suitable for audit tracing and sales mix review.",
            report,
            9,
            ExecutiveEmerald);

        var itemHeaders = new[] { "Card Code", "Card Name", "Item Code", "Item Name", "Invoices", "Qty Sold", "Sales USD", "Sales ZiG", "Value Pulse" };
        for (var index = 0; index < itemHeaders.Length; index++)
        {
            itemSheet.Cell(itemRow, index + 1).Value = itemHeaders[index];
        }
        StyleExecutiveTableHeader(itemSheet, itemRow, itemHeaders.Length, ExecutiveEmerald);
        var itemFreeze = itemRow;
        itemRow++;

        var itemRows = orderedAccounts
            .SelectMany(account => account.Items.Select(item => new
            {
                account.CardCode,
                account.CardName,
                item.ItemCode,
                item.ItemName,
                item.InvoiceCount,
                item.TotalQuantitySold,
                item.TotalSalesUsd,
                item.TotalSalesZig
            }))
            .OrderBy(rowItem => rowItem.CardCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rowItem => rowItem.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var itemSalesMax = Math.Max(1m, itemRows.Any() ? itemRows.Max(item => item.TotalSalesUsd) : 0m);
        if (itemRows.Any())
        {
            var dataStart = itemRow;
            foreach (var item in itemRows)
            {
                itemSheet.Cell(itemRow, 1).Value = item.CardCode;
                itemSheet.Cell(itemRow, 2).Value = item.CardName;
                itemSheet.Cell(itemRow, 3).Value = item.ItemCode;
                itemSheet.Cell(itemRow, 4).Value = item.ItemName;
                itemSheet.Cell(itemRow, 5).Value = item.InvoiceCount;
                itemSheet.Cell(itemRow, 6).Value = item.TotalQuantitySold;
                itemSheet.Cell(itemRow, 7).Value = item.TotalSalesUsd;
                itemSheet.Cell(itemRow, 8).Value = item.TotalSalesZig;
                itemSheet.Cell(itemRow, 9).Value = BuildExecutiveSignalBar(item.TotalSalesUsd, itemSalesMax);
                itemSheet.Cell(itemRow, 9).Style.Font.FontName = "Consolas";
                itemSheet.Range(itemRow, 6, itemRow, 8).Style.NumberFormat.Format = "#,##0.00";
                itemRow++;
            }

            StyleExecutiveTableRows(itemSheet, dataStart, itemRow - 1, itemHeaders.Length);
        }
        else
        {
            itemSheet.Range(itemRow, 1, itemRow, itemHeaders.Length).Merge();
            itemSheet.Cell(itemRow, 1).Value = "No item summary rows are available for this report.";
            itemSheet.Cell(itemRow, 1).Style.Font.Italic = true;
            itemSheet.Cell(itemRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            itemRow++;
        }

        WriteExecutiveFooter(itemSheet, itemRow + 1, 9);
        FinalizeExecutiveSheet(itemSheet, 9, itemFreeze, landscape: true);

        var invoiceSheet = workbook.Worksheets.Add("Invoice Register");
        ConfigureExecutiveSheet(invoiceSheet, 18, ExecutiveAmber);
        var invoiceRow = WriteExecutiveBannerSimple(
            invoiceSheet,
            "Invoice Register",
            "Full invoice-line drilldown with high-value highlighting and accounting-friendly alignment.",
            report,
            18,
            ExecutiveAmber);

        var invoiceHeaders = new[] { "Period", "Source", "Doc Date (CAT)", "Card Code", "Card Name", "Invoice #", "DocEntry", "Status", "Currency", "Invoice Total", "Value Band", "Line #", "Item Code", "Item Name", "Quantity", "Line Amount", "Sales USD", "Sales ZiG" };
        for (var index = 0; index < invoiceHeaders.Length; index++)
        {
            invoiceSheet.Cell(invoiceRow, index + 1).Value = invoiceHeaders[index];
        }
        StyleExecutiveTableHeader(invoiceSheet, invoiceRow, invoiceHeaders.Length, ExecutiveAmber);
        var invoiceFreeze = invoiceRow;
        invoiceRow++;

        if (report.InvoiceDetails.Any())
        {
            var dataStart = invoiceRow;
            foreach (var invoice in report.InvoiceDetails
                         .OrderBy(invoice => invoice.DocumentDateUtc)
                         .ThenBy(invoice => invoice.CardCode, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(invoice => invoice.DocumentNumber, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(invoice => invoice.LineNumber))
            {
                var isHighValue = invoice.DocumentTotal >= invoiceHighValueThreshold && invoiceHighValueThreshold > 0;

                invoiceSheet.Cell(invoiceRow, 1).Value = invoice.PeriodLabel;
                invoiceSheet.Cell(invoiceRow, 2).Value = invoice.Source;
                invoiceSheet.Cell(invoiceRow, 3).Value = FormatCatDate(invoice.DocumentDateUtc);
                invoiceSheet.Cell(invoiceRow, 4).Value = invoice.CardCode;
                invoiceSheet.Cell(invoiceRow, 5).Value = invoice.CardName;
                invoiceSheet.Cell(invoiceRow, 6).Value = invoice.DocumentNumber;
                invoiceSheet.Cell(invoiceRow, 7).Value = invoice.DocumentEntry;
                invoiceSheet.Cell(invoiceRow, 8).Value = invoice.Status;
                invoiceSheet.Cell(invoiceRow, 9).Value = invoice.Currency;
                invoiceSheet.Cell(invoiceRow, 10).Value = invoice.DocumentTotal;
                invoiceSheet.Cell(invoiceRow, 11).Value = isHighValue ? "High Value" : "Standard";
                invoiceSheet.Cell(invoiceRow, 12).Value = invoice.LineNumber;
                invoiceSheet.Cell(invoiceRow, 13).Value = invoice.ItemCode;
                invoiceSheet.Cell(invoiceRow, 14).Value = invoice.ItemName;
                invoiceSheet.Cell(invoiceRow, 15).Value = invoice.QuantitySold;
                invoiceSheet.Cell(invoiceRow, 16).Value = invoice.LineAmount;
                invoiceSheet.Cell(invoiceRow, 17).Value = invoice.SalesUsd;
                invoiceSheet.Cell(invoiceRow, 18).Value = invoice.SalesZig;

                if (isHighValue)
                {
                    invoiceSheet.Range(invoiceRow, 1, invoiceRow, invoiceHeaders.Length).Style.Fill.BackgroundColor = ExecutiveSoftRose;
                }

                invoiceSheet.Range(invoiceRow, 10, invoiceRow, 18).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveSourceBadge(invoiceSheet.Cell(invoiceRow, 2));
                ApplyExecutiveStatusBadge(invoiceSheet.Cell(invoiceRow, 8));
                ApplyExecutiveValueBandBadge(invoiceSheet.Cell(invoiceRow, 11));
                invoiceRow++;
            }

            StyleExecutiveTableRows(invoiceSheet, dataStart, invoiceRow - 1, invoiceHeaders.Length, preserveExistingFill: true);
        }
        else
        {
            invoiceSheet.Range(invoiceRow, 1, invoiceRow, invoiceHeaders.Length).Merge();
            invoiceSheet.Cell(invoiceRow, 1).Value = "No invoice line detail is available for this report.";
            invoiceSheet.Cell(invoiceRow, 1).Style.Font.Italic = true;
            invoiceSheet.Cell(invoiceRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            invoiceRow++;
        }

        WriteExecutiveFooter(invoiceSheet, invoiceRow + 1, 18);
        FinalizeExecutiveSheet(invoiceSheet, 18, invoiceFreeze, landscape: true);

        var paymentSheet = workbook.Worksheets.Add("Payment Register");
        ConfigureExecutiveSheet(paymentSheet, 15, ExecutiveRose);
        var paymentRow = WriteExecutiveBannerSimple(
            paymentSheet,
            "Payment Register",
            "Incoming payment drilldown with reference tracking, settlement posture, and value highlighting.",
            report,
            15,
            ExecutiveRose);

        var paymentHeaders = new[] { "Period", "Source", "Payment Date (CAT)", "Card Code", "Card Name", "Payment #", "DocEntry", "Status", "Currency", "Total Amount", "Incoming USD", "Incoming ZiG", "Applied Invoices", "Reference", "Value Band" };
        for (var index = 0; index < paymentHeaders.Length; index++)
        {
            paymentSheet.Cell(paymentRow, index + 1).Value = paymentHeaders[index];
        }
        StyleExecutiveTableHeader(paymentSheet, paymentRow, paymentHeaders.Length, ExecutiveRose);
        var paymentFreeze = paymentRow;
        paymentRow++;

        if (report.PaymentDetails.Any())
        {
            var dataStart = paymentRow;
            foreach (var payment in report.PaymentDetails
                         .OrderBy(payment => payment.PaymentDateUtc)
                         .ThenBy(payment => payment.CardCode, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(payment => payment.PaymentNumber, StringComparer.OrdinalIgnoreCase))
            {
                var isHighValue = payment.TotalAmount >= paymentHighValueThreshold && paymentHighValueThreshold > 0;

                paymentSheet.Cell(paymentRow, 1).Value = payment.PeriodLabel;
                paymentSheet.Cell(paymentRow, 2).Value = payment.Source;
                paymentSheet.Cell(paymentRow, 3).Value = FormatCatDate(payment.PaymentDateUtc);
                paymentSheet.Cell(paymentRow, 4).Value = payment.CardCode;
                paymentSheet.Cell(paymentRow, 5).Value = payment.CardName;
                paymentSheet.Cell(paymentRow, 6).Value = payment.PaymentNumber;
                paymentSheet.Cell(paymentRow, 7).Value = payment.PaymentEntry;
                paymentSheet.Cell(paymentRow, 8).Value = payment.Status;
                paymentSheet.Cell(paymentRow, 9).Value = payment.Currency;
                paymentSheet.Cell(paymentRow, 10).Value = payment.TotalAmount;
                paymentSheet.Cell(paymentRow, 11).Value = payment.IncomingPaymentsUsd;
                paymentSheet.Cell(paymentRow, 12).Value = payment.IncomingPaymentsZig;
                paymentSheet.Cell(paymentRow, 13).Value = payment.AppliedInvoiceCount;
                paymentSheet.Cell(paymentRow, 14).Value = payment.Reference;
                paymentSheet.Cell(paymentRow, 15).Value = isHighValue ? "High Value" : "Standard";

                if (isHighValue)
                {
                    paymentSheet.Range(paymentRow, 1, paymentRow, paymentHeaders.Length).Style.Fill.BackgroundColor = ExecutiveSoftBlue;
                }

                paymentSheet.Range(paymentRow, 10, paymentRow, 12).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveSourceBadge(paymentSheet.Cell(paymentRow, 2));
                ApplyExecutiveStatusBadge(paymentSheet.Cell(paymentRow, 8));
                ApplyExecutiveValueBandBadge(paymentSheet.Cell(paymentRow, 15));
                paymentRow++;
            }

            StyleExecutiveTableRows(paymentSheet, dataStart, paymentRow - 1, paymentHeaders.Length, preserveExistingFill: true);
        }
        else
        {
            paymentSheet.Range(paymentRow, 1, paymentRow, paymentHeaders.Length).Merge();
            paymentSheet.Cell(paymentRow, 1).Value = "No incoming payment detail is available for this report.";
            paymentSheet.Cell(paymentRow, 1).Style.Font.Italic = true;
            paymentSheet.Cell(paymentRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            paymentRow++;
        }

        WriteExecutiveFooter(paymentSheet, paymentRow + 1, 15);
        FinalizeExecutiveSheet(paymentSheet, 15, paymentFreeze, landscape: true);

        var applicationSheet = workbook.Worksheets.Add("Application Map");
        ConfigureExecutiveSheet(applicationSheet, 12, ExecutiveRoyalBlue);
        var applicationRow = WriteExecutiveBannerSimple(
            applicationSheet,
            "Payment Application Map",
            "Document-to-payment application breakdown for allocation review and reconciliation.",
            report,
            12,
            ExecutiveRoyalBlue);

        var applicationHeaders = new[] { "Period", "Source", "Payment Date (CAT)", "Card Code", "Card Name", "Payment #", "DocEntry", "Status", "Applied Invoice", "Invoice Type", "Currency", "Applied Amount" };
        for (var index = 0; index < applicationHeaders.Length; index++)
        {
            applicationSheet.Cell(applicationRow, index + 1).Value = applicationHeaders[index];
        }
        StyleExecutiveTableHeader(applicationSheet, applicationRow, applicationHeaders.Length, ExecutiveRoyalBlue);
        var applicationFreeze = applicationRow;
        applicationRow++;

        if (report.PaymentApplications.Any())
        {
            var dataStart = applicationRow;
            foreach (var application in report.PaymentApplications
                         .OrderBy(application => application.PaymentDateUtc)
                         .ThenBy(application => application.CardCode, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(application => application.PaymentNumber, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(application => application.AppliedInvoiceReference, StringComparer.OrdinalIgnoreCase))
            {
                applicationSheet.Cell(applicationRow, 1).Value = application.PeriodLabel;
                applicationSheet.Cell(applicationRow, 2).Value = application.Source;
                applicationSheet.Cell(applicationRow, 3).Value = FormatCatDate(application.PaymentDateUtc);
                applicationSheet.Cell(applicationRow, 4).Value = application.CardCode;
                applicationSheet.Cell(applicationRow, 5).Value = application.CardName;
                applicationSheet.Cell(applicationRow, 6).Value = application.PaymentNumber;
                applicationSheet.Cell(applicationRow, 7).Value = application.PaymentEntry;
                applicationSheet.Cell(applicationRow, 8).Value = application.Status;
                applicationSheet.Cell(applicationRow, 9).Value = application.AppliedInvoiceReference;
                applicationSheet.Cell(applicationRow, 10).Value = application.InvoiceType;
                applicationSheet.Cell(applicationRow, 11).Value = application.Currency;
                applicationSheet.Cell(applicationRow, 12).Value = application.AppliedAmount;
                applicationSheet.Cell(applicationRow, 12).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveSourceBadge(applicationSheet.Cell(applicationRow, 2));
                ApplyExecutiveStatusBadge(applicationSheet.Cell(applicationRow, 8));
                applicationRow++;
            }

            StyleExecutiveTableRows(applicationSheet, dataStart, applicationRow - 1, applicationHeaders.Length, preserveExistingFill: true);
        }
        else
        {
            applicationSheet.Range(applicationRow, 1, applicationRow, applicationHeaders.Length).Merge();
            applicationSheet.Cell(applicationRow, 1).Value = "No payment application detail is available for this report.";
            applicationSheet.Cell(applicationRow, 1).Style.Font.Italic = true;
            applicationSheet.Cell(applicationRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            applicationRow++;
        }

        WriteExecutiveFooter(applicationSheet, applicationRow + 1, 12);
        FinalizeExecutiveSheet(applicationSheet, 12, applicationFreeze, landscape: true);

        var workbookBytes = WorkbookToBytes(workbook);
        return AddExecutiveChartsToAccountSalesWorkbook(
            workbookBytes,
            trendFreeze,
            trendDataStartRow,
            trendDataEndRow,
            accountFreeze,
            accountDataStartRow,
            accountChartDataEndRow);
    }

    private static void ConfigureExecutiveSheet(IXLWorksheet ws, int lastCol, XLColor tabColor)
    {
        ws.TabColor = tabColor;
        ws.ShowGridLines = false;
        ws.Columns(1, lastCol).Style.Fill.BackgroundColor = ExecutiveCanvas;
        ws.Columns(1, lastCol).Style.Font.FontName = "Segoe UI";
        ws.Columns(1, lastCol).Style.Font.FontColor = ExecutiveTextPrimary;
        ws.Columns(1, lastCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static int WriteExecutiveBanner(
        IXLWorksheet ws,
        string title,
        string subtitle,
        GetAccountSalesPaymentReportResult report,
        int lastCol)
    {
        var generatedAt = report.GeneratedAtUtc == default ? CurrentCatNow() : IAuditService.ToCAT(report.GeneratedAtUtc);
        var sourceLabel = report.Sources.Any() ? string.Join(", ", report.Sources) : "SAP";

        ws.Range(1, 1, 1, 3).Style.Fill.BackgroundColor = ExecutiveIndigo;
        ws.Range(1, 4, 1, 6).Style.Fill.BackgroundColor = ExecutiveRoyalBlue;
        ws.Range(1, 7, 1, 9).Style.Fill.BackgroundColor = ExecutiveCyan;
        ws.Range(1, 10, 1, 12).Style.Fill.BackgroundColor = ExecutiveEmerald;
        ws.Range(1, 13, 1, lastCol).Style.Fill.BackgroundColor = ExecutiveRose;
        ws.Row(1).Height = 8;

        ws.Range(2, 1, 6, lastCol).Style.Fill.BackgroundColor = ExecutiveIndigo;
        ws.Range(2, 1, 6, lastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(2, 1, 6, lastCol).Style.Border.OutsideBorderColor = ExecutiveRoyalBlue;

        ws.Range(2, 1, 6, 9).Merge();
        ws.Cell(3, 1).Value = title;
        ws.Cell(3, 1).Style.Font.Bold = true;
        ws.Cell(3, 1).Style.Font.FontName = "Segoe UI";
        ws.Cell(3, 1).Style.Font.FontSize = 24;
        ws.Cell(3, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(3, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Bottom;

        ws.Cell(4, 1).Value = subtitle;
        ws.Cell(4, 1).Style.Font.FontSize = 11;
        ws.Cell(4, 1).Style.Font.FontColor = XLColor.FromHtml("#C7D2FE");
        ws.Cell(4, 1).Style.Alignment.WrapText = true;

        ws.Range(5, 1, 5, 4).Merge();
        ws.Cell(5, 1).Value = string.Empty;
        ws.Range(5, 1, 5, 4).Style.Fill.BackgroundColor = ExecutiveCyan;
        ws.Row(5).Height = 6;

        ws.Range(6, 1, 6, 9).Merge();
        ws.Cell(6, 1).Value = $"DATE RANGE  {FormatCatDate(report.FromDateUtc)}  TO  {FormatCatDate(report.ToDateUtc)}";
        ws.Cell(6, 1).Style.Font.FontSize = 10;
        ws.Cell(6, 1).Style.Font.Bold = true;
        ws.Cell(6, 1).Style.Font.FontColor = XLColor.White;

        ws.Range(2, 10, 6, 12).Style.Fill.BackgroundColor = XLColor.FromHtml("#1D4ED8");
        ws.Range(2, 10, 6, 12).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(2, 10, 6, 12).Style.Border.OutsideBorderColor = XLColor.FromHtml("#93C5FD");
        ws.Range(2, 13, 6, lastCol).Style.Fill.BackgroundColor = ExecutiveSurface;
        ws.Range(2, 13, 6, lastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(2, 13, 6, lastCol).Style.Border.OutsideBorderColor = XLColor.FromHtml("#93C5FD");

        ws.Range(2, 10, 2, 12).Merge();
        ws.Cell(2, 10).Value = "EXECUTIVE SNAPSHOT";
        ws.Cell(2, 10).Style.Font.Bold = true;
        ws.Cell(2, 10).Style.Font.FontSize = 10;
        ws.Cell(2, 10).Style.Font.FontColor = XLColor.White;
        ws.Cell(2, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        ws.Range(3, 10, 3, 12).Merge();
        ws.Cell(3, 10).Value = $"Requested accounts: {report.Summary.RequestedAccountCount:N0}  |  Active: {report.Summary.ActiveAccountCount:N0}";
        ws.Cell(3, 10).Style.Font.FontSize = 11;
        ws.Cell(3, 10).Style.Font.FontColor = XLColor.White;
        ws.Cell(3, 10).Style.Alignment.WrapText = true;

        ws.Range(4, 10, 4, 12).Merge();
        ws.Cell(4, 10).Value = $"Sources: {sourceLabel}  |  Grouping: {report.Grouping}";
        ws.Cell(4, 10).Style.Font.FontSize = 10;
        ws.Cell(4, 10).Style.Font.FontColor = XLColor.FromHtml("#DBEAFE");
        ws.Cell(4, 10).Style.Alignment.WrapText = true;

        ws.Range(5, 10, 5, 12).Merge();
        ws.Cell(5, 10).Value = $"Company: {CompanyName}";
        ws.Cell(5, 10).Style.Font.FontSize = 10;
        ws.Cell(5, 10).Style.Font.FontColor = XLColor.FromHtml("#DBEAFE");

        ws.Range(6, 10, 6, 12).Merge();
        ws.Cell(6, 10).Value = $"Generated: {generatedAt:dd MMM yyyy HH:mm} CAT";
        ws.Cell(6, 10).Style.Font.FontSize = 10;
        ws.Cell(6, 10).Style.Font.Bold = true;
        ws.Cell(6, 10).Style.Font.FontColor = XLColor.White;

        ws.Range(2, 13, 2, lastCol).Merge();
        ws.Cell(2, 13).Value = "KEFALOS BRAND MARK";
        ws.Cell(2, 13).Style.Font.Bold = true;
        ws.Cell(2, 13).Style.Font.FontSize = 9;
        ws.Cell(2, 13).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Cell(2, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range(6, 13, 6, lastCol).Merge();
        ws.Cell(6, 13).Value = "Accounting-grade executive workbook";
        ws.Cell(6, 13).Style.Font.FontSize = 9;
        ws.Cell(6, 13).Style.Font.FontColor = ExecutiveTextMuted;
        ws.Cell(6, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Rows(2, 6).Height = 24;
        return 8;
    }

    private static int WriteExecutiveBannerSimple(
        IXLWorksheet ws,
        string title,
        string subtitle,
        GetAccountSalesPaymentReportResult report,
        int lastCol,
        XLColor accentColor)
    {
        ws.Range(1, 1, 1, lastCol).Style.Fill.BackgroundColor = accentColor;
        ws.Row(1).Height = 6;

        ws.Range(2, 1, 5, lastCol).Style.Fill.BackgroundColor = ExecutiveSurface;
        ws.Range(2, 1, 5, lastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(2, 1, 5, lastCol).Style.Border.OutsideBorderColor = ExecutiveBorder;

        ws.Range(2, 1, 2, lastCol).Merge();
        ws.Cell(2, 1).Value = title;
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.FontSize = 18;
        ws.Cell(2, 1).Style.Font.FontColor = ExecutiveTextPrimary;

        ws.Range(3, 1, 3, lastCol).Merge();
        ws.Cell(3, 1).Value = subtitle;
        ws.Cell(3, 1).Style.Font.FontSize = 10;
        ws.Cell(3, 1).Style.Font.FontColor = ExecutiveTextSecondary;

        ws.Range(4, 1, 4, lastCol).Merge();
        ws.Cell(4, 1).Value = $"Report window {FormatCatDate(report.FromDateUtc)} to {FormatCatDate(report.ToDateUtc)}  |  Grouping {report.Grouping}  |  Generated {FormatCatDateTime(report.GeneratedAtUtc == default ? DateTime.UtcNow : report.GeneratedAtUtc)} CAT";
        ws.Cell(4, 1).Style.Font.FontSize = 9;
        ws.Cell(4, 1).Style.Font.FontColor = ExecutiveTextMuted;

        ws.Range(5, 1, 5, lastCol).Style.Fill.BackgroundColor = ExecutiveSection;
        ws.Row(5).Height = 4;
        return 7;
    }

    private static void WriteExecutiveKpiCard(
        IXLWorksheet ws,
        int topRow,
        int startCol,
        int endCol,
        XLColor accentColor,
        string label,
        string value,
        string supportingText)
    {
        ws.Range(topRow, startCol, topRow + 4, endCol).Style.Fill.BackgroundColor = ExecutiveSurface;
        ws.Range(topRow, startCol, topRow + 4, endCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(topRow, startCol, topRow + 4, endCol).Style.Border.OutsideBorderColor = ExecutiveBorder;
        ws.Range(topRow, startCol, topRow, endCol).Style.Fill.BackgroundColor = accentColor;
        ws.Row(topRow).Height = 8;

        ws.Range(topRow + 1, startCol, topRow + 1, endCol).Merge();
        ws.Cell(topRow + 1, startCol).Value = label;
        ws.Cell(topRow + 1, startCol).Style.Font.Bold = true;
        ws.Cell(topRow + 1, startCol).Style.Font.FontSize = 9;
        ws.Cell(topRow + 1, startCol).Style.Font.FontColor = ExecutiveTextMuted;
        ws.Cell(topRow + 1, startCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        ws.Range(topRow + 2, startCol, topRow + 3, endCol).Merge();
        ws.Cell(topRow + 2, startCol).Value = value;
        ws.Cell(topRow + 2, startCol).Style.Font.Bold = true;
        ws.Cell(topRow + 2, startCol).Style.Font.FontSize = 17;
        ws.Cell(topRow + 2, startCol).Style.Font.FontColor = ExecutiveTextPrimary;
        ws.Cell(topRow + 2, startCol).Style.Alignment.WrapText = true;
        ws.Cell(topRow + 2, startCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Cell(topRow + 2, startCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Range(topRow + 4, startCol, topRow + 4, endCol).Merge();
        ws.Cell(topRow + 4, startCol).Value = supportingText;
        ws.Cell(topRow + 4, startCol).Style.Font.FontSize = 9;
        ws.Cell(topRow + 4, startCol).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Cell(topRow + 4, startCol).Style.Alignment.WrapText = true;
        ws.Row(topRow + 2).Height = 22;
        ws.Row(topRow + 3).Height = 22;
        ws.Row(topRow + 4).Height = 28;
    }

    private static void WriteExecutiveCallout(IXLWorksheet ws, int topRow, int lastCol, string label, string narrative)
    {
        ws.Range(topRow, 1, topRow + 2, lastCol).Style.Fill.BackgroundColor = ExecutiveSection;
        ws.Range(topRow, 1, topRow + 2, lastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(topRow, 1, topRow + 2, lastCol).Style.Border.OutsideBorderColor = ExecutiveBorder;
        ws.Range(topRow, 1, topRow, 1).Style.Fill.BackgroundColor = ExecutiveRoyalBlue;
        ws.Range(topRow + 1, 1, topRow + 2, 1).Style.Fill.BackgroundColor = ExecutiveRoyalBlue;

        ws.Range(topRow, 2, topRow, lastCol).Merge();
        ws.Cell(topRow, 2).Value = label;
        ws.Cell(topRow, 2).Style.Font.Bold = true;
        ws.Cell(topRow, 2).Style.Font.FontSize = 11;
        ws.Cell(topRow, 2).Style.Font.FontColor = ExecutiveIndigo;

        ws.Range(topRow + 1, 2, topRow + 2, lastCol).Merge();
        ws.Cell(topRow + 1, 2).Value = narrative;
        ws.Cell(topRow + 1, 2).Style.Font.FontSize = 10;
        ws.Cell(topRow + 1, 2).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Cell(topRow + 1, 2).Style.Alignment.WrapText = true;
        ws.Rows(topRow, topRow + 2).Height = 26;
    }

    private static void WriteExecutiveSectionHeader(IXLWorksheet ws, int row, int lastCol, string title, string subtitle, XLColor accentColor)
    {
        ws.Range(row, 1, row + 1, lastCol).Style.Fill.BackgroundColor = ExecutiveSurface;
        ws.Range(row, 1, row + 1, lastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(row, 1, row + 1, lastCol).Style.Border.OutsideBorderColor = ExecutiveBorder;
        ws.Range(row, 1, row + 1, 1).Style.Fill.BackgroundColor = accentColor;

        ws.Range(row, 2, row, lastCol).Merge();
        ws.Cell(row, 2).Value = title;
        ws.Cell(row, 2).Style.Font.Bold = true;
        ws.Cell(row, 2).Style.Font.FontSize = 12;
        ws.Cell(row, 2).Style.Font.FontColor = ExecutiveTextPrimary;

        ws.Range(row + 1, 2, row + 1, lastCol).Merge();
        ws.Cell(row + 1, 2).Value = subtitle;
        ws.Cell(row + 1, 2).Style.Font.FontSize = 9;
        ws.Cell(row + 1, 2).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Row(row).Height = 20;
        ws.Row(row + 1).Height = 18;
    }

    private static void WriteExecutiveChartContainer(
        IXLWorksheet ws,
        int topRow,
        int leftCol,
        int bottomRow,
        int rightCol,
        string title,
        string subtitle,
        XLColor accentColor)
    {
        ws.Range(topRow, leftCol, bottomRow, rightCol).Style.Fill.BackgroundColor = ExecutiveSurface;
        ws.Range(topRow, leftCol, bottomRow, rightCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(topRow, leftCol, bottomRow, rightCol).Style.Border.OutsideBorderColor = ExecutiveBorder;
        ws.Range(topRow, leftCol, topRow, rightCol).Merge();
        ws.Range(topRow, leftCol, topRow, rightCol).Style.Fill.BackgroundColor = accentColor;
        ws.Cell(topRow, leftCol).Value = title;
        ws.Cell(topRow, leftCol).Style.Font.Bold = true;
        ws.Cell(topRow, leftCol).Style.Font.FontSize = 10;
        ws.Cell(topRow, leftCol).Style.Font.FontColor = XLColor.White;
        ws.Cell(topRow, leftCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range(topRow + 1, leftCol, topRow + 1, rightCol).Merge();
        ws.Cell(topRow + 1, leftCol).Value = subtitle;
        ws.Cell(topRow + 1, leftCol).Style.Font.FontSize = 9;
        ws.Cell(topRow + 1, leftCol).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Cell(topRow + 1, leftCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(topRow + 1, leftCol).Style.Alignment.WrapText = true;
    }

    private static void StyleExecutiveTableHeader(IXLWorksheet ws, int headerRow, int lastCol, XLColor accentColor)
    {
        var headerRange = ws.Range(headerRow, 1, headerRow, lastCol);
        headerRange.Style.Fill.BackgroundColor = accentColor;
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontSize = 10;
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        headerRange.Style.Border.BottomBorderColor = XLColor.White;
        ws.Row(headerRow).Height = 24;
    }

    private static void StyleExecutiveTableRows(IXLWorksheet ws, int firstRow, int lastRow, int lastCol, bool preserveExistingFill = false)
    {
        if (lastRow < firstRow)
        {
            return;
        }

        for (var row = firstRow; row <= lastRow; row++)
        {
            var rowRange = ws.Range(row, 1, row, lastCol);
            if (!preserveExistingFill || rowRange.Style.Fill.BackgroundColor == XLColor.NoColor || rowRange.Style.Fill.BackgroundColor == XLColor.Transparent)
            {
                rowRange.Style.Fill.BackgroundColor = (row - firstRow) % 2 == 0 ? ExecutiveSurface : ExecutiveCanvas;
            }

            rowRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            rowRange.Style.Border.BottomBorderColor = ExecutiveBorder;
            rowRange.Style.Font.FontSize = 10;
            rowRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
    }

    private static void FinalizeExecutiveSheet(IXLWorksheet ws, int lastCol, int freezeRow = 0, bool landscape = false)
    {
        ws.Columns(1, lastCol).AdjustToContents();
        for (var col = 1; col <= lastCol; col++)
        {
            if (ws.Column(col).Width > 34)
            {
                ws.Column(col).Width = 34;
            }

            if (ws.Column(col).Width < 10)
            {
                ws.Column(col).Width = 10;
            }
        }

        if (freezeRow > 0)
        {
            ws.SheetView.FreezeRows(freezeRow);
        }

        ws.PageSetup.PageOrientation = landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetLeft(0.35);
        ws.PageSetup.Margins.SetRight(0.35);
        ws.PageSetup.Margins.SetTop(0.45);
        ws.PageSetup.Margins.SetBottom(0.45);
    }

    private static void WriteExecutiveFooter(IXLWorksheet ws, int row, int colSpan)
    {
        var generatedAt = CurrentCatNow();
        ws.Range(row, 1, row, colSpan).Merge();
        ws.Range(row, 1, row, colSpan).Style.Fill.BackgroundColor = ExecutiveSection;
        ws.Range(row, 1, row, colSpan).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        ws.Range(row, 1, row, colSpan).Style.Border.TopBorderColor = ExecutiveBorder;
        ws.Cell(row, 1).Value = $"CONFIDENTIAL  |  {CompanyName}  |  {SystemName}  |  Generated {generatedAt:dd MMM yyyy HH:mm} CAT";
        ws.Cell(row, 1).Style.Font.FontSize = 8;
        ws.Cell(row, 1).Style.Font.FontColor = ExecutiveTextMuted;
        ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static string FormatExecutiveMoneyPair(decimal usd, decimal zig) =>
        $"USD {usd:N2}\nZiG {zig:N2}";

    private static string FormatExecutivePercent(decimal value) => $"{value:N2}%";

    private static decimal CalculateExecutivePercent(decimal numerator, decimal denominator) =>
        denominator <= 0 ? 0m : Math.Round((numerator / denominator) * 100m, 2);

    private static string BuildExecutiveSignalBar(decimal value, decimal maxValue, int width = 12)
    {
        if (maxValue <= 0)
        {
            return new string('-', width);
        }

        var ratio = Math.Max(0m, Math.Min(1m, value / maxValue));
        var filled = (int)Math.Round(ratio * width, MidpointRounding.AwayFromZero);
        return new string('#', filled).PadRight(width, '-');
    }

    private static string ResolveExecutiveCollectionStatus(decimal outstandingUsd, decimal outstandingZig, decimal collectionUsd, decimal collectionZig)
    {
        if (outstandingUsd <= 0 && outstandingZig <= 0)
        {
            return "Paid";
        }

        if (collectionUsd >= 85m || collectionZig >= 85m)
        {
            return "Watch";
        }

        return "Outstanding";
    }

    private static void ApplyExecutiveSourceBadge(IXLCell cell)
    {
        var source = cell.GetString().Trim();
        cell.Style.Font.Bold = true;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = ExecutiveBorder;

        if (source.Equals("API", StringComparison.OrdinalIgnoreCase))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftIndigo;
            cell.Style.Font.FontColor = ExecutiveIndigo;
        }
        else
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftBlue;
            cell.Style.Font.FontColor = ExecutiveRoyalBlue;
        }
    }

    private static void ApplyExecutiveStatusBadge(IXLCell cell)
    {
        var status = cell.GetString().Trim();
        var normalizedStatus = status.ToUpperInvariant();

        cell.Style.Font.Bold = true;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = ExecutiveBorder;

        if (normalizedStatus.Contains("PAID") || normalizedStatus.Contains("COMPLETED") || normalizedStatus.Contains("POSTED") || normalizedStatus.Contains("SYNCED"))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftEmerald;
            cell.Style.Font.FontColor = ExecutiveEmerald;
        }
        else if (normalizedStatus.Contains("OUTSTANDING") || normalizedStatus.Contains("FAILED") || normalizedStatus.Contains("VOID") || normalizedStatus.Contains("CANCEL"))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftRose;
            cell.Style.Font.FontColor = ExecutiveRose;
        }
        else
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftAmber;
            cell.Style.Font.FontColor = ExecutiveAmber;
        }
    }

    private static void ApplyExecutiveValueBandBadge(IXLCell cell)
    {
        var band = cell.GetString().Trim();
        cell.Style.Font.Bold = true;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = ExecutiveBorder;

        if (band.Equals("High Value", StringComparison.OrdinalIgnoreCase))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftRose;
            cell.Style.Font.FontColor = ExecutiveRose;
        }
        else
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftBlue;
            cell.Style.Font.FontColor = ExecutiveRoyalBlue;
        }
    }

    private static void ApplyExecutiveOutstandingStyle(IXLCell cell, decimal value)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = ExecutiveBorder;

        if (value <= 0)
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftEmerald;
            cell.Style.Font.FontColor = ExecutiveEmerald;
        }
        else if (value < 1000m)
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftAmber;
            cell.Style.Font.FontColor = ExecutiveAmber;
        }
        else
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftRose;
            cell.Style.Font.FontColor = ExecutiveRose;
        }
    }

    private static decimal CalculateExecutiveHighValueThreshold(IEnumerable<decimal> values)
    {
        var orderedValues = values
            .Where(value => value > 0)
            .OrderBy(value => value)
            .ToList();

        if (orderedValues.Count == 0)
        {
            return 0m;
        }

        var percentileIndex = (int)Math.Floor((orderedValues.Count - 1) * 0.85m);
        return orderedValues[Math.Clamp(percentileIndex, 0, orderedValues.Count - 1)];
    }

    private static string? ResolveExecutiveLogoPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, BrandLogoRelativePath),
            Path.Combine(Directory.GetCurrentDirectory(), BrandLogoRelativePath),
            Path.Combine(Directory.GetCurrentDirectory(), "ShopInventory.Web", BrandLogoRelativePath),
            Path.Combine(AppContext.BaseDirectory, "images", "kefalos-logo.jpg")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryAddExecutiveLogo(IXLWorksheet ws, string? logoPath, int row, int col, double scale)
    {
        if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath))
        {
            return;
        }

        try
        {
            var picture = ws.AddPicture(logoPath, $"Kefalos_{ws.Name}_{row}_{col}");
            picture.MoveTo(ws.Cell(row, col), 10, 10);
            picture.Scale(scale, true);
            picture.WithPlacement(XLPicturePlacement.FreeFloating);
        }
        catch
        {
            // Branding is decorative. Export should still succeed if the asset cannot be loaded.
        }
    }

    private static byte[] AddExecutiveChartsToAccountSalesWorkbook(
        byte[] workbookBytes,
        int trendHeaderRow,
        int trendDataStartRow,
        int trendDataEndRow,
        int accountHeaderRow,
        int accountDataStartRow,
        int accountDataEndRow)
    {
        if (trendDataEndRow < trendDataStartRow && accountDataEndRow < accountDataStartRow)
        {
            return workbookBytes;
        }

        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            if (trendDataEndRow >= trendDataStartRow)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Visuals",
                    chartName: "Period Sales Collections",
                    sourceSheetName: "Trend Analysis",
                    headerRow: trendHeaderRow,
                    categoryColumn: 1,
                    dataStartRow: trendDataStartRow,
                    dataEndRow: trendDataEndRow,
                    seriesColumns: new[] { 7, 8 },
                    seriesColors: new[] { "2563EB", "10B981" },
                    fromColumn: 0,
                    fromRow: 13,
                    toColumn: 7,
                    toRow: 29);
            }

            if (accountDataEndRow >= accountDataStartRow)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Visuals",
                    chartName: "Top Accounts Sales Outstanding",
                    sourceSheetName: "Customer Analysis",
                    headerRow: accountHeaderRow,
                    categoryColumn: 1,
                    dataStartRow: accountDataStartRow,
                    dataEndRow: accountDataEndRow,
                    seriesColumns: new[] { 5, 7 },
                    seriesColors: new[] { "2563EB", "F43F5E" },
                    fromColumn: 7,
                    fromRow: 13,
                    toColumn: 14,
                    toRow: 29);
            }
        }

        return stream.ToArray();
    }

    private static void AddExecutiveClusteredColumnChart(
        SpreadsheetDocument document,
        string targetSheetName,
        string chartName,
        string sourceSheetName,
        int headerRow,
        int categoryColumn,
        int dataStartRow,
        int dataEndRow,
        IReadOnlyList<int> seriesColumns,
        IReadOnlyList<string> seriesColors,
        int fromColumn,
        int fromRow,
        int toColumn,
        int toRow)
    {
        if (seriesColumns.Count == 0 || seriesColumns.Count != seriesColors.Count)
        {
            return;
        }

        var workbookPart = document.WorkbookPart;
        if (workbookPart is null)
        {
            return;
        }

        var targetWorksheetPart = GetWorksheetPartByName(workbookPart, targetSheetName);
        if (targetWorksheetPart is null)
        {
            return;
        }

        var drawingsPart = EnsureDrawingsPart(targetWorksheetPart);
        var chartPart = drawingsPart.AddNewPart<ChartPart>();

        BuildExecutiveClusteredColumnChart(
            chartPart,
            sourceSheetName,
            headerRow,
            categoryColumn,
            dataStartRow,
            dataEndRow,
            seriesColumns,
            seriesColors);

        AppendChartAnchor(drawingsPart, chartPart, chartName, fromColumn, fromRow, toColumn, toRow);
    }

    private static void BuildExecutiveClusteredColumnChart(
        ChartPart chartPart,
        string sourceSheetName,
        int headerRow,
        int categoryColumn,
        int dataStartRow,
        int dataEndRow,
        IReadOnlyList<int> seriesColumns,
        IReadOnlyList<string> seriesColors)
    {
        var chartSpace = new C.ChartSpace();
        chartSpace.Append(new C.EditingLanguage { Val = "en-US" });

        var chart = chartSpace.AppendChild(new C.Chart());
        chart.Append(new C.AutoTitleDeleted { Val = true });

        var plotArea = chart.AppendChild(new C.PlotArea());
        plotArea.AppendChild(new C.Layout());

        var barChart = plotArea.AppendChild(new C.BarChart());
        barChart.Append(new C.BarDirection { Val = C.BarDirectionValues.Column });
        barChart.Append(new C.BarGrouping { Val = C.BarGroupingValues.Clustered });
        barChart.Append(new C.VaryColors { Val = false });

        var categoryFormula = BuildSheetRangeFormula(sourceSheetName, dataStartRow, categoryColumn, dataEndRow, categoryColumn);

        for (var index = 0; index < seriesColumns.Count; index++)
        {
            var series = new C.BarChartSeries();
            series.Append(new C.Index { Val = (uint)index });
            series.Append(new C.Order { Val = (uint)index });

            var seriesText = new C.SeriesText();
            var stringReference = new C.StringReference();
            stringReference.Append(new C.Formula(BuildSheetCellFormula(sourceSheetName, headerRow, seriesColumns[index])));
            seriesText.Append(stringReference);
            series.Append(seriesText);

            series.Append(new C.InvertIfNegative { Val = false });
            series.Append(new C.ChartShapeProperties(
                new A.SolidFill(new A.RgbColorModelHex { Val = seriesColors[index] }),
                new A.Outline(new A.NoFill())));

            var categoryAxisData = new C.CategoryAxisData();
            var categoryReference = new C.StringReference();
            categoryReference.Append(new C.Formula(categoryFormula));
            categoryAxisData.Append(categoryReference);
            series.Append(categoryAxisData);

            var values = new C.Values();
            var numberReference = new C.NumberReference();
            numberReference.Append(new C.Formula(BuildSheetRangeFormula(sourceSheetName, dataStartRow, seriesColumns[index], dataEndRow, seriesColumns[index])));
            values.Append(numberReference);
            series.Append(values);

            barChart.Append(series);
        }

        barChart.Append(new C.DataLabels(
            new C.ShowLegendKey { Val = false },
            new C.ShowValue { Val = false },
            new C.ShowCategoryName { Val = false },
            new C.ShowSeriesName { Val = false },
            new C.ShowPercent { Val = false },
            new C.ShowBubbleSize { Val = false }));
        barChart.Append(new C.GapWidth { Val = 65 });

        var categoryAxisId = (uint)(48650112 + (Math.Abs(sourceSheetName.GetHashCode()) % 1000) * 2);
        var valueAxisId = categoryAxisId + 1;

        barChart.Append(new C.AxisId { Val = categoryAxisId });
        barChart.Append(new C.AxisId { Val = valueAxisId });

        var categoryAxis = new C.CategoryAxis();
        categoryAxis.Append(new C.AxisId { Val = categoryAxisId });
        categoryAxis.Append(new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }));
        categoryAxis.Append(new C.Delete { Val = false });
        categoryAxis.Append(new C.AxisPosition { Val = C.AxisPositionValues.Bottom });
        categoryAxis.Append(new C.NumberingFormat { FormatCode = "General", SourceLinked = true });
        categoryAxis.Append(new C.MajorTickMark { Val = C.TickMarkValues.None });
        categoryAxis.Append(new C.MinorTickMark { Val = C.TickMarkValues.None });
        categoryAxis.Append(new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo });
        categoryAxis.Append(new C.CrossingAxis { Val = valueAxisId });
        categoryAxis.Append(new C.Crosses { Val = C.CrossesValues.AutoZero });
        categoryAxis.Append(new C.AutoLabeled { Val = true });
        categoryAxis.Append(new C.LabelAlignment { Val = C.LabelAlignmentValues.Center });
        categoryAxis.Append(new C.LabelOffset { Val = 100 });

        var valueAxis = new C.ValueAxis();
        valueAxis.Append(new C.AxisId { Val = valueAxisId });
        valueAxis.Append(new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }));
        valueAxis.Append(new C.Delete { Val = false });
        valueAxis.Append(new C.AxisPosition { Val = C.AxisPositionValues.Left });
        valueAxis.Append(new C.MajorGridlines());
        valueAxis.Append(new C.NumberingFormat { FormatCode = "#,##0.00", SourceLinked = false });
        valueAxis.Append(new C.MajorTickMark { Val = C.TickMarkValues.None });
        valueAxis.Append(new C.MinorTickMark { Val = C.TickMarkValues.None });
        valueAxis.Append(new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo });
        valueAxis.Append(new C.CrossingAxis { Val = categoryAxisId });
        valueAxis.Append(new C.Crosses { Val = C.CrossesValues.AutoZero });
        valueAxis.Append(new C.CrossBetween { Val = C.CrossBetweenValues.Between });

        plotArea.Append(categoryAxis);
        plotArea.Append(valueAxis);

        chart.Append(new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Bottom }, new C.Layout()));
        chart.Append(new C.PlotVisibleOnly { Val = true });
        chart.Append(new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });

        chartPart.ChartSpace = chartSpace;
        chartPart.ChartSpace.Save();
    }

    private static WorksheetPart? GetWorksheetPartByName(WorkbookPart workbookPart, string worksheetName)
    {
        var sheet = workbookPart.Workbook.Descendants<Sheet>()
            .FirstOrDefault(candidate => string.Equals(candidate.Name?.Value, worksheetName, StringComparison.OrdinalIgnoreCase));

        return sheet?.Id?.Value is { Length: > 0 } relationshipId
            ? (WorksheetPart)workbookPart.GetPartById(relationshipId)
            : null;
    }

    private static DrawingsPart EnsureDrawingsPart(WorksheetPart worksheetPart)
    {
        if (worksheetPart.DrawingsPart is not null)
        {
            worksheetPart.DrawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
            return worksheetPart.DrawingsPart;
        }

        var drawingsPart = worksheetPart.AddNewPart<DrawingsPart>();
        drawingsPart.WorksheetDrawing = new Xdr.WorksheetDrawing();
        worksheetPart.Worksheet.Append(new DocumentFormat.OpenXml.Spreadsheet.Drawing { Id = worksheetPart.GetIdOfPart(drawingsPart) });
        worksheetPart.Worksheet.Save();
        return drawingsPart;
    }

    private static void AppendChartAnchor(
        DrawingsPart drawingsPart,
        ChartPart chartPart,
        string chartName,
        int fromColumn,
        int fromRow,
        int toColumn,
        int toRow)
    {
        drawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();

        var drawingId = (uint)(drawingsPart.WorksheetDrawing.ChildElements.Count + 2);
        var chartRelationshipId = drawingsPart.GetIdOfPart(chartPart);

        var twoCellAnchor = drawingsPart.WorksheetDrawing.AppendChild(new Xdr.TwoCellAnchor());
        twoCellAnchor.Append(new Xdr.FromMarker(
            new Xdr.ColumnId(fromColumn.ToString()),
            new Xdr.ColumnOffset("0"),
            new Xdr.RowId(fromRow.ToString()),
            new Xdr.RowOffset("0")));
        twoCellAnchor.Append(new Xdr.ToMarker(
            new Xdr.ColumnId(toColumn.ToString()),
            new Xdr.ColumnOffset("0"),
            new Xdr.RowId(toRow.ToString()),
            new Xdr.RowOffset("0")));

        var graphicFrame = twoCellAnchor.AppendChild(new Xdr.GraphicFrame { Macro = string.Empty });
        graphicFrame.Append(new Xdr.NonVisualGraphicFrameProperties(
            new Xdr.NonVisualDrawingProperties { Id = drawingId, Name = chartName },
            new Xdr.NonVisualGraphicFrameDrawingProperties()));

        var transform = new Xdr.Transform();
        transform.Append(new A.Offset { X = 0L, Y = 0L });
        transform.Append(new A.Extents { Cx = 0L, Cy = 0L });
        graphicFrame.Append(transform);

        var graphic = new A.Graphic();
        var graphicData = new A.GraphicData { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" };
        graphicData.Append(new C.ChartReference { Id = chartRelationshipId });
        graphic.Append(graphicData);
        graphicFrame.Append(graphic);

        twoCellAnchor.Append(new Xdr.ClientData());
        drawingsPart.WorksheetDrawing.Save();
    }

    private static string BuildSheetRangeFormula(string sheetName, int startRow, int startColumn, int endRow, int endColumn) =>
        $"'{sheetName.Replace("'", "''")}'!${ToExcelColumnName(startColumn)}${startRow}:${ToExcelColumnName(endColumn)}${endRow}";

    private static string BuildSheetCellFormula(string sheetName, int row, int column) =>
        $"'{sheetName.Replace("'", "''")}'!${ToExcelColumnName(column)}${row}";

    private static string ToExcelColumnName(int columnNumber)
    {
        var dividend = columnNumber;
        var columnName = string.Empty;

        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar(65 + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName;
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