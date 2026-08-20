using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ShopInventory.Web.Common;
using ShopInventory.Web.Features.Reports.Queries.GetAccountSalesPaymentReport;
using ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;
using ShopInventory.Web.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;
using ShopInventory.Web.Models;
using System.Globalization;
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
    byte[] ExportVanAttendanceReportToExcel(VanVisitReportResponse report, DateTime? fromDate = null, DateTime? toDate = null);

    byte[] ExportVanSalesPerformanceToExcel(VanSalesPerformanceReportResponse report);

    byte[] ExportVanSalesCoverageToExcel(VanSalesCoverageReportResponse report);

    byte[] ExportVanReplenishmentToExcel(VanReplenishmentReportResponse report);

    byte[] ExportVanStockToExcel(VanStockReportResponse report);
    byte[] ExportDesktopSalesToExcel(List<DesktopSaleDto> sales, EndOfDayReportDto? report, DateTime? fromDate = null, DateTime? toDate = null);
    byte[] ExportLocalStockToExcel(LocalStockResultDto stock);
    byte[] ExportAccountSalesPaymentReportToExcel(GetAccountSalesPaymentReportResult report);
    byte[] ExportItemVolumeSalesReportToExcel(GetItemVolumeSalesReportResult report, string title);
    byte[] ExportMerchandiserPurchaseOrderReportToExcel(GetMerchandiserPurchaseOrderReportResult report);
    byte[] ExportMobileOrdersToExcel(IReadOnlyCollection<SalesOrderDto> orders, string title);
    byte[] ExportRouteCustomerSalesToExcel(RouteCustomerSalesDetailModel detail, string routeLabel);
    byte[] ExportRouteSalesSummaryToExcel(
        RouteCustomerSalesSummaryModel summary,
        IReadOnlyDictionary<string, string> routeLabels);
    byte[] ExportGLAccountLedgerToExcel(GLAccountLedgerResponse ledger);
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
    private static readonly XLColor ReportSurface = XLColor.FromHtml("#ffffff");
    private static readonly XLColor ReportBorder = XLColor.FromHtml("#d0d7e8");
    private static readonly XLColor MutedText = XLColor.FromHtml("#616161");
    private static readonly XLColor FaintText = XLColor.FromHtml("#9e9e9e");

    /// <summary>
    /// The typeface every workbook is written in. Matching the Office default keeps
    /// the reports from rendering in a substitute font on a machine without it.
    /// </summary>
    private const string ReportFont = "Aptos";

    // Number formats.
    //
    // Two things every money column here needs. Negatives are red and in brackets,
    // because a credit note or a stock adjustment that reads "-1,204.00" in the same
    // weight as the rest of the column is the figure people misread. And each format
    // names its own currency: these reports pair a USD column with a ZiG one, and once
    // the header row has scrolled away an unlabelled "#,##0.00" is unattributable.
    private const string FormatUsd = "$#,##0.00;[Red]($#,##0.00)";
    private const string FormatZig = "\"ZiG\" #,##0.00;[Red](\"ZiG\" #,##0.00)";
    private const string FormatMoney = "#,##0.00;[Red](#,##0.00)";
    private const string FormatQuantity = "#,##0.00";
    // Two decimals, matching the screen. The report rounds volume to two where the
    // conversion factor is applied, so this format hides nothing.
    private const string FormatVolume = "#,##0.00";
    private const string FormatCount = "#,##0";
    private const string FormatPercent = "0.0%";
    private const string FormatDate = "dd MMM yyyy";
    private const string FormatDayDate = "ddd, dd MMM yyyy";
    private const string FormatTimestamp = "dd MMM yyyy HH:mm";

    private const double MinColumnWidth = 9;
    private const double MaxColumnWidth = 42;

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime CurrentCatNow() => IAuditService.ToCAT(DateTime.UtcNow);

    private static string FormatCatDateTime(DateTime utcDateTime) =>
        IAuditService.ToCAT(EnsureUtc(utcDateTime)).ToString("dd MMM yyyy HH:mm");

    private static string FormatCatDate(DateTime utcDateTime) =>
        IAuditService.ToCAT(EnsureUtc(utcDateTime)).ToString("dd MMM yyyy");

    private static decimal CalculatePendingLineValue(OrderLineDetail line) =>
        line.QuantityOrdered > 0
            ? Math.Round(line.LineTotal * line.QuantityPending / line.QuantityOrdered, 2)
            : 0;

    /// <summary>
    /// Opens a workbook with the house font and fills in the document properties, so
    /// the file carries its own identity once it has been mailed on and detached from
    /// the download name.
    /// </summary>
    private static XLWorkbook NewWorkbook(string title)
    {
        var workbook = new XLWorkbook();
        workbook.Style.Font.FontName = ReportFont;
        workbook.Style.Font.FontSize = 10;
        workbook.Properties.Title = title;
        workbook.Properties.Subject = title;
        workbook.Properties.Company = CompanyName;
        workbook.Properties.Author = SystemName;
        workbook.Properties.Created = CurrentCatNow();
        return workbook;
    }

    /// <summary>
    /// Creates the professional report header on a worksheet and returns the next available row.
    /// </summary>
    /// <remarks>
    /// Rows 1 to 6 are the letterhead and row 7 is the first free row, which is the
    /// contract every caller is written against \u2014 the band is restyled here rather
    /// than re-laid-out.
    /// </remarks>
    private static int WriteReportHeader(IXLWorksheet ws, string reportTitle, int colSpan, DateTime? fromDate = null, DateTime? toDate = null, string? subtitle = null)
    {
        var generatedAt = CurrentCatNow();

        ApplySheetDefaults(ws);

        // Accent rule across the top, then the letterhead on a white card. Reports are
        // read on screen far more often than printed, and the sheet's own grid behind
        // a styled band is the thing that makes an export look unfinished.
        ws.Range(1, 1, 1, colSpan).Style.Fill.BackgroundColor = NavyBlue;
        ws.Row(1).Height = 6;

        var card = ws.Range(2, 1, 5, colSpan);
        card.Style.Fill.BackgroundColor = ReportSurface;
        card.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        card.Style.Border.OutsideBorderColor = ReportBorder;

        // Company name
        ws.Range(2, 1, 2, colSpan).Merge();
        ws.Cell(2, 1).Value = CompanyName;
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.FontSize = 16;
        ws.Cell(2, 1).Style.Font.FontColor = NavyBlue;
        ws.Cell(2, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Cell(2, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(2).Height = 24;

        // System subtitle
        ws.Range(3, 1, 3, colSpan).Merge();
        ws.Cell(3, 1).Value = SystemName;
        ws.Cell(3, 1).Style.Font.FontSize = 9;
        ws.Cell(3, 1).Style.Font.FontColor = MutedText;
        ws.Cell(3, 1).Style.Font.Italic = true;

        // Report title
        ws.Range(4, 1, 4, colSpan).Merge();
        ws.Cell(4, 1).Value = reportTitle;
        ws.Cell(4, 1).Style.Font.Bold = true;
        ws.Cell(4, 1).Style.Font.FontSize = 14;
        ws.Cell(4, 1).Style.Font.FontColor = LightNavy;
        ws.Cell(4, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(4).Height = 22;

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
        ws.Cell(5, 1).Style.Font.FontSize = 9;
        ws.Cell(5, 1).Style.Font.FontColor = MutedText;

        // Spacer between the letterhead and whatever the sheet opens with.
        ws.Range(6, 1, 6, colSpan).Style.Fill.BackgroundColor = AccentBlue;
        ws.Row(6).Height = 6;

        return 7; // next available row
    }

    private static void ApplySheetDefaults(IXLWorksheet ws, XLColor? tabColor = null)
    {
        ws.ShowGridLines = false;
        ws.Style.Font.FontName = ReportFont;
        ws.Style.Font.FontSize = 10;
        if (tabColor is not null) ws.TabColor = tabColor;
    }

    /// <summary>
    /// Adds a coloured, defaulted sheet. Excel rejects a sheet name past 31 characters
    /// with an exception rather than a truncation, so the trim happens here once
    /// instead of at whichever call site next composes a name from report data.
    /// </summary>
    private static IXLWorksheet AddSheet(XLWorkbook workbook, string name, XLColor? tabColor = null)
    {
        var sheet = workbook.Worksheets.Add(name.Length > 31 ? name[..31] : name);
        ApplySheetDefaults(sheet, tabColor ?? NavyBlue);
        return sheet;
    }

    /// <summary>
    /// Writes a KPI metric card (2 rows: value on top, label below).
    /// </summary>
    private static void WriteKpiCard(IXLWorksheet ws, int row, int col, string label, string value, XLColor? valueColor = null)
    {
        StyleKpiCard(ws, row, col, label, valueColor);
        ws.Cell(row, col).Value = value;
    }

    /// <summary>
    /// A KPI card holding a real number rather than a pre-formatted string, so the
    /// headline figure can be pointed at by a formula and reads in the same format as
    /// the column it summarises.
    /// </summary>
    private static void WriteKpiCard(IXLWorksheet ws, int row, int col, string label, decimal value, string numberFormat, XLColor? valueColor = null)
    {
        StyleKpiCard(ws, row, col, label, valueColor);
        ws.Cell(row, col).Value = value;
        ws.Cell(row, col).Style.NumberFormat.Format = numberFormat;
    }

    private static void StyleKpiCard(IXLWorksheet ws, int row, int col, string label, XLColor? valueColor)
    {
        ws.Cell(row, col).Style.Font.Bold = true;
        ws.Cell(row, col).Style.Font.FontSize = 15;
        ws.Cell(row, col).Style.Font.FontColor = valueColor ?? NavyBlue;
        ws.Cell(row, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, col).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        // The columns beneath are sized to the table, not to these cards, so a headline
        // figure can be wider than the column it sits in. Shrink-to-fit scales it down
        // instead of letting Excel render a numeric cell as ####.
        ws.Cell(row, col).Style.Alignment.ShrinkToFit = true;
        ws.Cell(row, col).Style.Fill.BackgroundColor = KpiBackground;
        ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row, col).Style.Border.OutsideBorderColor = ReportBorder;
        ws.Row(row).Height = 26;

        ws.Cell(row + 1, col).Value = label;
        ws.Cell(row + 1, col).Style.Font.FontSize = 8;
        ws.Cell(row + 1, col).Style.Font.FontColor = MutedText;
        ws.Cell(row + 1, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row + 1, col).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ws.Cell(row + 1, col).Style.Alignment.WrapText = true;
        ws.Cell(row + 1, col).Style.Fill.BackgroundColor = KpiBackground;
        ws.Cell(row + 1, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row + 1, col).Style.Border.OutsideBorderColor = ReportBorder;
        ws.Cell(row + 1, col).Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        ws.Cell(row + 1, col).Style.Border.BottomBorderColor = LightNavy;
        ws.Row(row + 1).Height = 24;
    }

    /// <summary>
    /// Writes a 2-column KPI metric row (label: value) for summary sections.
    /// </summary>
    private static void WriteKpiRow(IXLWorksheet ws, int row, string label, string value, bool highlight = false)
    {
        StyleKpiRow(ws, row, label, highlight);
        ws.Cell(row, 2).Value = value;
    }

    private static void WriteKpiRow(IXLWorksheet ws, int row, string label, decimal value, string numberFormat, bool highlight = false)
    {
        StyleKpiRow(ws, row, label, highlight);
        ws.Cell(row, 2).Value = value;
        ws.Cell(row, 2).Style.NumberFormat.Format = numberFormat;
    }

    private static void StyleKpiRow(IXLWorksheet ws, int row, string label, bool highlight)
    {
        ws.Cell(row, 1).Value = label;
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 10;
        ws.Cell(row, 1).Style.Fill.BackgroundColor = highlight ? TotalsBackground : KpiBackground;
        ws.Cell(row, 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row, 1).Style.Border.OutsideBorderColor = ReportBorder;
        ws.Cell(row, 1).Style.Alignment.Indent = 1;

        ws.Cell(row, 2).Style.Font.FontSize = 11;
        ws.Cell(row, 2).Style.Font.Bold = highlight;
        ws.Cell(row, 2).Style.Fill.BackgroundColor = highlight ? TotalsBackground : KpiBackground;
        ws.Cell(row, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row, 2).Style.Border.OutsideBorderColor = ReportBorder;
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
        headerRange.Style.Font.FontSize = 9;
        headerRange.Style.Fill.BackgroundColor = NavyBlue;
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        // Headings wrap rather than widen. Without this a column is only ever as narrow
        // as its own title, so "Suggested Order" sets the width of a column of 3-digit
        // numbers.
        headerRange.Style.Alignment.WrapText = true;
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.OutsideBorderColor = LightNavy;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        headerRange.Style.Border.BottomBorderColor = XLColor.FromHtml("#0d47a1");
        ws.Row(headerRow).Height = 30;
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
        dataRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        for (int r = firstDataRow; r <= lastRow; r++)
        {
            ws.Range(r, 1, r, lastCol).Style.Fill.BackgroundColor =
                (r - firstDataRow) % 2 == 1 ? LightGray : ReportSurface;
        }
    }

    /// <summary>
    /// Closes off a data table: stripes and borders the rows, hangs the filter
    /// dropdowns off the header, and stands in a line of text when the query came back
    /// empty. Returns the next free row.
    /// </summary>
    /// <remarks>
    /// A sheet showing a header and then nothing reads as a broken export rather than
    /// as a quiet week, and it is the version of the report that gets forwarded back
    /// with a question attached.
    /// </remarks>
    private static int FinishTable(
        IXLWorksheet ws,
        int headerRow,
        int firstDataRow,
        int nextRow,
        int lastCol,
        string emptyMessage = "No records matched this report's filters.",
        bool filter = true)
    {
        var lastDataRow = nextRow - 1;

        if (lastDataRow < firstDataRow)
        {
            ws.Range(firstDataRow, 1, firstDataRow, lastCol).Merge();
            ws.Cell(firstDataRow, 1).Value = emptyMessage;
            ws.Cell(firstDataRow, 1).Style.Font.Italic = true;
            ws.Cell(firstDataRow, 1).Style.Font.FontColor = MutedText;
            ws.Cell(firstDataRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(firstDataRow, 1).Style.Fill.BackgroundColor = ReportSurface;
            ws.Range(firstDataRow, 1, firstDataRow, lastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws.Range(firstDataRow, 1, firstDataRow, lastCol).Style.Border.OutsideBorderColor = BorderGray;
            ws.Row(firstDataRow).Height = 22;
            return firstDataRow + 1;
        }

        StyleDataRows(ws, firstDataRow, lastDataRow, lastCol);

        // Excel allows one filter per sheet, so a second table on the same sheet keeps
        // the first one's dropdowns rather than throwing.
        if (filter && !ws.AutoFilter.IsEnabled)
        {
            ws.Range(headerRow, 1, lastDataRow, lastCol).SetAutoFilter();
        }

        return nextRow;
    }

    /// <summary>
    /// A rule-and-caption above a block, for the sheets that stack more than one.
    /// </summary>
    private static void WriteSectionTitle(IXLWorksheet ws, int row, int lastCol, string title)
    {
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Value = title;
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 11;
        ws.Cell(row, 1).Style.Font.FontColor = LightNavy;
        ws.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Bottom;
        ws.Range(row, 1, row, lastCol).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        ws.Range(row, 1, row, lastCol).Style.Border.BottomBorderColor = LightNavy;
        ws.Row(row).Height = 20;
    }

    /// <summary>
    /// Writes an identifier that is usually all digits — a document number, a user code — as a
    /// number wherever it truly is one, so Excel stops flagging the column as "number stored as
    /// text" and marking every row with a green triangle.
    /// </summary>
    /// <remarks>
    /// Only when the digits round-trip exactly. "0041" is not the number 41: a code with a leading
    /// zero loses its identity the moment it is written as one, and SAP's originating-document
    /// number is a varchar that is free to hold something that is not a number at all. Alignment is
    /// set on both branches, or a column that converted some of its rows and not others would sit
    /// half left and half right.
    /// </remarks>
    private static void WriteIdentifier(
        IXLCell cell,
        string? value,
        XLAlignmentHorizontalValues alignment = XLAlignmentHorizontalValues.Left)
    {
        var text = value?.Trim() ?? string.Empty;

        if (text.Length > 0
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number.ToString(CultureInfo.InvariantCulture) == text)
        {
            cell.Value = number;
            // No thousands separator: "872,071" is not a number anybody can look up.
            cell.Style.NumberFormat.Format = "0";
        }
        else
        {
            cell.Value = text.Length == 0 ? "-" : text;
        }

        cell.Style.Alignment.Horizontal = alignment;
    }

    /// <summary>
    /// A full-width advisory band — the sheet's version of the banner a page shows above its
    /// figures. Returns the next free row.
    /// </summary>
    /// <remarks>
    /// For the caveat that has to travel with the numbers rather than sit beside them on a screen:
    /// once a workbook has been mailed on, a sheet that does not say its figures are in doubt is
    /// read as a sheet whose figures are sound.
    /// </remarks>
    private static int WriteNotice(IXLWorksheet ws, int row, int colSpan, string text, XLColor accent)
    {
        var band = ws.Range(row, 1, row, colSpan);
        band.Merge();
        band.Style.Fill.BackgroundColor = KpiBackground;
        band.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        band.Style.Border.OutsideBorderColor = ReportBorder;
        band.Style.Border.LeftBorder = XLBorderStyleValues.Thick;
        band.Style.Border.LeftBorderColor = accent;

        ws.Cell(row, 1).Value = text;
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.FontColor = accent;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(row, 1).Style.Alignment.Indent = 1;

        // A merged cell does not grow to fit its own wrapped text, so the height is measured here
        // or the notice is clipped to its first line — which is the half that says there is a
        // problem without saying what it is. Ten characters a column is about what these sheets
        // settle at once FinalizeSheet has clamped the widths.
        var lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)(colSpan * 10)));
        ws.Row(row).Height = Math.Max(18, lines * 13);

        return row + 1;
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
        ws.Row(row).Height = 20;
    }

    /// <summary>
    /// One totals row per currency, for the registers that carry more than one. A
    /// single sum down a mixed column is not a number in any currency.
    /// </summary>
    private static int WriteCurrencyTotals<T>(
        IXLWorksheet ws,
        int row,
        int lastCol,
        int currencyColumn,
        int labelColumn,
        IEnumerable<IGrouping<string, T>> groups,
        Action<IXLWorksheet, int, IGrouping<string, T>> writeAmounts)
    {
        foreach (var group in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ws.Cell(row, labelColumn).Value = $"Total ({group.Count():N0})";
            ws.Cell(row, labelColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, currencyColumn).Value = group.Key;
            ws.Cell(row, currencyColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            writeAmounts(ws, row, group);
            StyleTotalsRow(ws, row, lastCol);
            row++;
        }

        return row;
    }

    /// <summary>
    /// Totals a column with SUBTOTAL rather than a figure computed in C#, so the total
    /// follows the filter: narrow the table to one customer and the row underneath is
    /// that customer's total instead of a number that no longer ties to anything on
    /// screen.
    /// </summary>
    private static void WriteSubtotal(IXLWorksheet ws, int row, int col, int firstDataRow, int lastDataRow, string numberFormat)
    {
        var cell = ws.Cell(row, col);

        if (lastDataRow >= firstDataRow)
        {
            var column = ToExcelColumnName(col);
            cell.FormulaA1 = $"SUBTOTAL(109,{column}{firstDataRow}:{column}{lastDataRow})";
        }
        else
        {
            cell.Value = 0;
        }

        cell.Style.NumberFormat.Format = numberFormat;
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
        ws.Cell(row, 1).Style.Font.FontColor = FaintText;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    /// <summary>
    /// Final adjustments: auto-fit columns, freeze header, page setup.
    /// </summary>
    /// <remarks>
    /// <paramref name="freezeRow"/> is the table's header row, so it doubles as the
    /// point below which the column widths are measured. Fitting the whole sheet lets
    /// a KPI card reading "$12,480,933.10" set the width of the "Rank" column beneath
    /// it, which is most of why these sheets opened looking uneven.
    /// </remarks>
    /// <param name="fitFromRow">
    /// Overrides where width measurement starts, for the sheets whose table should set
    /// the column widths but whose header is not worth freezing.
    /// </param>
    private static void FinalizeSheet(IXLWorksheet ws, int lastCol, int freezeRow = 0, bool landscape = false, int fitFromRow = 0)
    {
        FitColumns(ws, lastCol, fitFromRow > 0 ? fitFromRow : freezeRow);

        if (freezeRow > 0)
        {
            ws.SheetView.FreezeRows(freezeRow);
            // Page 2 of a printed register is unreadable without its headings.
            ws.PageSetup.SetRowsToRepeatAtTop(freezeRow, freezeRow);
        }

        ws.PageSetup.PageOrientation = landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetLeft(0.4);
        ws.PageSetup.Margins.SetRight(0.4);
        ws.PageSetup.Margins.SetTop(0.5);
        ws.PageSetup.Margins.SetBottom(0.5);
        ApplyPrintHeaderFooter(ws);
    }

    /// <summary>
    /// Sizes the columns from the table band down, then clamps them: wide enough that
    /// a heading is legible, narrow enough that one long product description does not
    /// push the money columns off the page.
    /// </summary>
    private static void FitColumns(IXLWorksheet ws, int lastCol, int fromRow)
    {
        var lastUsedRow = ws.LastRowUsed()?.RowNumber() ?? 0;

        for (int c = 1; c <= lastCol; c++)
        {
            var column = ws.Column(c);

            if (fromRow > 0 && lastUsedRow >= fromRow)
                column.AdjustToContents(fromRow, lastUsedRow);
            else
                column.AdjustToContents();

            if (column.Width > MaxColumnWidth) column.Width = MaxColumnWidth;
            if (column.Width < MinColumnWidth) column.Width = MinColumnWidth;
        }
    }

    /// <summary>
    /// The printed page's own header and footer \u2014 the sheet body's footer row only
    /// ever lands on the last page, so a loose page off a long register was otherwise
    /// unattributable and unnumbered.
    /// </summary>
    private static void ApplyPrintHeaderFooter(IXLWorksheet ws)
    {
        ws.PageSetup.Header.Left.AddText(CompanyName);
        ws.PageSetup.Header.Right.AddText(ws.Name);
        ws.PageSetup.Footer.Left.AddText($"CONFIDENTIAL \u2022 {SystemName}");
        ws.PageSetup.Footer.Right.AddText("Page ");
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber);
        ws.PageSetup.Footer.Right.AddText(" of ");
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages);
    }

    // ═══════════════════════════════════════════════════════════════
    // SALES SUMMARY
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportSalesSummaryToExcel(SalesSummaryReport report)
    {
        using var workbook = NewWorkbook("Sales Summary Report");

        // ── Dashboard Sheet ──
        var dash = AddSheet(workbook, "Sales Dashboard");
        int row = WriteReportHeader(dash, "Sales Summary Report", 6, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total Invoices", report.TotalInvoices, FormatCount);
        WriteKpiCard(dash, row, 2, "Total Sales (USD)", report.TotalSalesUSD, FormatUsd);
        WriteKpiCard(dash, row, 3, "Total Sales (ZiG)", report.TotalSalesZIG, FormatZig);
        WriteKpiCard(dash, row, 4, "VAT (USD)", report.TotalVatUSD, FormatUsd);
        WriteKpiCard(dash, row, 5, "Avg Invoice (USD)", report.AverageInvoiceValueUSD, FormatUsd);
        WriteKpiCard(dash, row, 6, "Unique Customers", report.UniqueCustomers, FormatCount);
        row += 3;

        WriteSectionTitle(dash, row, 6, "SALES BY CURRENCY");
        row++;

        dash.Cell(row, 1).Value = "Currency"; dash.Cell(row, 2).Value = "Invoices";
        dash.Cell(row, 3).Value = "Total Sales"; dash.Cell(row, 4).Value = "Total VAT";
        StyleTableHeader(dash, row, 4);
        int currencyHeader = row;
        row++;
        int dataStart = row;
        foreach (var curr in report.SalesByCurrency)
        {
            dash.Cell(row, 1).Value = curr.Currency;
            dash.Cell(row, 2).Value = curr.InvoiceCount; dash.Cell(row, 2).Style.NumberFormat.Format = FormatCount;
            dash.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            // Each row is a different currency, so the amounts carry no symbol and the
            // column is deliberately left without a total.
            dash.Cell(row, 3).Value = curr.TotalSales; dash.Cell(row, 3).Style.NumberFormat.Format = FormatMoney;
            dash.Cell(row, 4).Value = curr.TotalVat; dash.Cell(row, 4).Style.NumberFormat.Format = FormatMoney;
            row++;
        }
        row = FinishTable(dash, currencyHeader, dataStart, row, 4, "No invoices were raised in this period.", filter: false);

        WriteFooter(dash, row, 6);
        FinalizeSheet(dash, 6, landscape: true);

        // ── Daily Breakdown Sheet ──
        var daily = AddSheet(workbook, "Daily Sales");
        int dRow = WriteReportHeader(daily, "Daily Sales Breakdown", 4, report.FromDate, report.ToDate);

        daily.Cell(dRow, 1).Value = "Date"; daily.Cell(dRow, 2).Value = "Invoices";
        daily.Cell(dRow, 3).Value = "Sales (USD)"; daily.Cell(dRow, 4).Value = "Sales (ZiG)";
        StyleTableHeader(daily, dRow, 4);
        int freezeAt = dRow;
        dRow++;
        int dailyStart = dRow;
        foreach (var day in report.DailySales.OrderByDescending(d => d.Date))
        {
            // A real date, not its rendering: written as text the column cannot be
            // sorted, filtered to a week or subtracted from anything.
            daily.Cell(dRow, 1).Value = day.Date;
            daily.Cell(dRow, 1).Style.NumberFormat.Format = FormatDayDate;
            daily.Cell(dRow, 2).Value = day.InvoiceCount;
            daily.Cell(dRow, 2).Style.NumberFormat.Format = FormatCount;
            daily.Cell(dRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            daily.Cell(dRow, 3).Value = day.TotalSalesUSD; daily.Cell(dRow, 3).Style.NumberFormat.Format = FormatUsd;
            daily.Cell(dRow, 4).Value = day.TotalSalesZIG; daily.Cell(dRow, 4).Style.NumberFormat.Format = FormatZig;
            dRow++;
        }
        int dailyLast = dRow - 1;
        dRow = FinishTable(daily, freezeAt, dailyStart, dRow, 4, "No invoices were raised in this period.");

        daily.Cell(dRow, 1).Value = "TOTAL";
        WriteSubtotal(daily, dRow, 2, dailyStart, dailyLast, FormatCount);
        daily.Cell(dRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(daily, dRow, 3, dailyStart, dailyLast, FormatUsd);
        WriteSubtotal(daily, dRow, 4, dailyStart, dailyLast, FormatZig);
        StyleTotalsRow(daily, dRow, 4);

        WriteFooter(daily, dRow, 4);
        FinalizeSheet(daily, 4, freezeAt);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // TOP PRODUCTS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportTopProductsToExcel(TopProductsReport report)
    {
        using var workbook = NewWorkbook("Top Products Report");
        var ws = AddSheet(workbook, "Top Products");
        int row = WriteReportHeader(ws, "Top Products Report", 7, report.FromDate, report.ToDate);

        WriteKpiCard(ws, row, 1, "Total Products Sold", report.TotalProductsSold, FormatCount);
        WriteKpiCard(ws, row, 2, "Products Listed", report.TopProducts.Count, FormatCount);
        WriteKpiCard(ws, row, 3, "Total Revenue (USD)", report.TopProducts.Sum(p => p.TotalRevenueUSD), FormatUsd);
        WriteKpiCard(ws, row, 4, "Total Orders", report.TopProducts.Sum(p => p.TimesOrdered), FormatCount);
        row += 3;

        ws.Cell(row, 1).Value = "Rank"; ws.Cell(row, 2).Value = "Item Code"; ws.Cell(row, 3).Value = "Product Name";
        ws.Cell(row, 4).Value = "Qty Sold"; ws.Cell(row, 5).Value = "Times Ordered";
        ws.Cell(row, 6).Value = "Revenue (USD)"; ws.Cell(row, 7).Value = "Revenue (ZiG)";
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
            ws.Cell(row, 4).Value = p.TotalQuantitySold; ws.Cell(row, 4).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 5).Value = p.TimesOrdered; ws.Cell(row, 5).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 6).Value = p.TotalRevenueUSD; ws.Cell(row, 6).Style.NumberFormat.Format = FormatUsd;
            ws.Cell(row, 7).Value = p.TotalRevenueZIG; ws.Cell(row, 7).Style.NumberFormat.Format = FormatZig;
            row++;
        }
        int lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, 7, "No products were sold in this period.");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 4, dataStart, lastData, FormatCount);
        WriteSubtotal(ws, row, 5, dataStart, lastData, FormatCount);
        WriteSubtotal(ws, row, 6, dataStart, lastData, FormatUsd);
        WriteSubtotal(ws, row, 7, dataStart, lastData, FormatZig);
        StyleTotalsRow(ws, row, 7);

        WriteFooter(ws, row, 7);
        FinalizeSheet(ws, 7, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // STOCK SUMMARY
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportStockSummaryToExcel(StockSummaryReport report)
    {
        using var workbook = NewWorkbook("Stock Summary Report");
        var ws = AddSheet(workbook, "Stock Summary");
        int row = WriteReportHeader(ws, "Stock Summary Report", 6, subtitle: $"Report Date: {report.ReportDate:dd MMM yyyy}");

        WriteKpiCard(ws, row, 1, "Total Products", report.TotalProducts, FormatCount);
        WriteKpiCard(ws, row, 2, "In Stock", report.ProductsInStock, FormatCount, SuccessGreen);
        WriteKpiCard(ws, row, 3, "Out of Stock", report.ProductsOutOfStock, FormatCount, DangerRed);
        WriteKpiCard(ws, row, 4, "Below Reorder", report.ProductsBelowReorderLevel, FormatCount, WarningOrange);
        WriteKpiCard(ws, row, 5, "Stock Value (USD)", report.TotalStockValueUSD, FormatUsd);
        WriteKpiCard(ws, row, 6, "Stock Value (ZiG)", report.TotalStockValueZIG, FormatZig);
        row += 3;

        ws.Cell(row, 1).Value = "Warehouse Code"; ws.Cell(row, 2).Value = "Warehouse Name";
        ws.Cell(row, 3).Value = "Products"; ws.Cell(row, 4).Value = "Total Qty";
        ws.Cell(row, 5).Value = "Value (USD)"; ws.Cell(row, 6).Value = "Value (ZiG)";
        StyleTableHeader(ws, row, 6);
        int freezeAt = row;
        row++;
        int dataStart = row;
        foreach (var wh in report.StockByWarehouse.OrderByDescending(w => w.TotalQuantity))
        {
            ws.Cell(row, 1).Value = wh.WarehouseCode;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = wh.WarehouseName;
            ws.Cell(row, 3).Value = wh.ProductCount; ws.Cell(row, 3).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 4).Value = wh.TotalQuantity; ws.Cell(row, 4).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 5).Value = wh.TotalValueUSD; ws.Cell(row, 5).Style.NumberFormat.Format = FormatUsd;
            ws.Cell(row, 6).Value = wh.TotalValueZIG; ws.Cell(row, 6).Style.NumberFormat.Format = FormatZig;
            row++;
        }
        int lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, 6, "No warehouse stock was returned for this snapshot.");

        // Products are counted once per warehouse, so summing the column would
        // double-count anything stocked in two of them: the report's own figure stands.
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 3).Value = report.TotalProducts; ws.Cell(row, 3).Style.NumberFormat.Format = FormatCount;
        WriteSubtotal(ws, row, 4, dataStart, lastData, FormatCount);
        WriteSubtotal(ws, row, 5, dataStart, lastData, FormatUsd);
        WriteSubtotal(ws, row, 6, dataStart, lastData, FormatZig);
        StyleTotalsRow(ws, row, 6);

        WriteFooter(ws, row, 6);
        FinalizeSheet(ws, 6, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // PAYMENT SUMMARY
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportPaymentSummaryToExcel(PaymentSummaryReport report)
    {
        using var workbook = NewWorkbook("Payment Summary Report");

        // ── Dashboard Sheet ──
        var dash = AddSheet(workbook, "Payment Dashboard");
        int row = WriteReportHeader(dash, "Payment Summary Report", 5, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total Payments", report.TotalPayments, FormatCount);
        WriteKpiCard(dash, row, 2, "Total (USD)", report.TotalAmountUSD, FormatUsd);
        WriteKpiCard(dash, row, 3, "Total (ZiG)", report.TotalAmountZIG, FormatZig);
        row += 3;

        WriteSectionTitle(dash, row, 5, "PAYMENT METHODS BREAKDOWN");
        row++;

        dash.Cell(row, 1).Value = "Payment Method"; dash.Cell(row, 2).Value = "Count";
        dash.Cell(row, 3).Value = "Amount (USD)"; dash.Cell(row, 4).Value = "Amount (ZiG)";
        dash.Cell(row, 5).Value = "% of Total";
        StyleTableHeader(dash, row, 5);
        int methodHeader = row;
        row++;
        int dataStart = row;
        foreach (var m in report.PaymentsByMethod.OrderByDescending(x => x.TotalAmountUSD))
        {
            dash.Cell(row, 1).Value = m.PaymentMethod;
            dash.Cell(row, 1).Style.Font.Bold = true;
            dash.Cell(row, 2).Value = m.PaymentCount; dash.Cell(row, 2).Style.NumberFormat.Format = FormatCount;
            dash.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            dash.Cell(row, 3).Value = m.TotalAmountUSD; dash.Cell(row, 3).Style.NumberFormat.Format = FormatUsd;
            dash.Cell(row, 4).Value = m.TotalAmountZIG; dash.Cell(row, 4).Style.NumberFormat.Format = FormatZig;
            dash.Cell(row, 5).Value = m.PercentageOfTotal / 100; dash.Cell(row, 5).Style.NumberFormat.Format = FormatPercent;
            dash.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;
        }
        int lastMethod = row - 1;
        row = FinishTable(dash, methodHeader, dataStart, row, 5, "No payments were received in this period.");

        dash.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(dash, row, 2, dataStart, lastMethod, FormatCount);
        dash.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(dash, row, 3, dataStart, lastMethod, FormatUsd);
        WriteSubtotal(dash, row, 4, dataStart, lastMethod, FormatZig);
        WriteSubtotal(dash, row, 5, dataStart, lastMethod, FormatPercent);
        dash.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(dash, row, 5);

        WriteFooter(dash, row, 5);
        FinalizeSheet(dash, 5, methodHeader, landscape: true);

        // ── Daily Payments Sheet ──
        var daily = AddSheet(workbook, "Daily Payments");
        int dRow = WriteReportHeader(daily, "Daily Payments Breakdown", 4, report.FromDate, report.ToDate);

        daily.Cell(dRow, 1).Value = "Date"; daily.Cell(dRow, 2).Value = "Count";
        daily.Cell(dRow, 3).Value = "Amount (USD)"; daily.Cell(dRow, 4).Value = "Amount (ZiG)";
        StyleTableHeader(daily, dRow, 4);
        int freezeAt = dRow;
        dRow++;
        int dailyStart = dRow;
        foreach (var d in report.DailyPayments.OrderByDescending(d => d.Date))
        {
            daily.Cell(dRow, 1).Value = d.Date;
            daily.Cell(dRow, 1).Style.NumberFormat.Format = FormatDayDate;
            daily.Cell(dRow, 2).Value = d.PaymentCount; daily.Cell(dRow, 2).Style.NumberFormat.Format = FormatCount;
            daily.Cell(dRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            daily.Cell(dRow, 3).Value = d.TotalAmountUSD; daily.Cell(dRow, 3).Style.NumberFormat.Format = FormatUsd;
            daily.Cell(dRow, 4).Value = d.TotalAmountZIG; daily.Cell(dRow, 4).Style.NumberFormat.Format = FormatZig;
            dRow++;
        }
        int lastDaily = dRow - 1;
        dRow = FinishTable(daily, freezeAt, dailyStart, dRow, 4, "No payments were received in this period.");

        daily.Cell(dRow, 1).Value = "TOTAL";
        WriteSubtotal(daily, dRow, 2, dailyStart, lastDaily, FormatCount);
        daily.Cell(dRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(daily, dRow, 3, dailyStart, lastDaily, FormatUsd);
        WriteSubtotal(daily, dRow, 4, dailyStart, lastDaily, FormatZig);
        StyleTotalsRow(daily, dRow, 4);

        WriteFooter(daily, dRow, 4);
        FinalizeSheet(daily, 4, freezeAt);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // TOP CUSTOMERS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportTopCustomersToExcel(TopCustomersReport report)
    {
        using var workbook = NewWorkbook("Top Customers Report");
        var ws = AddSheet(workbook, "Top Customers");
        int row = WriteReportHeader(ws, "Top Customers Report", 8, report.FromDate, report.ToDate);

        WriteKpiCard(ws, row, 1, "Total Customers", report.TotalCustomers, FormatCount);
        WriteKpiCard(ws, row, 2, "Customers Listed", report.TopCustomers.Count, FormatCount);
        WriteKpiCard(ws, row, 3, "Total Purchases (USD)", report.TopCustomers.Sum(c => c.TotalPurchasesUSD), FormatUsd);
        WriteKpiCard(ws, row, 4, "Total Outstanding (USD)", report.TopCustomers.Sum(c => c.OutstandingBalanceUSD), FormatUsd, DangerRed);
        row += 3;

        ws.Cell(row, 1).Value = "Rank"; ws.Cell(row, 2).Value = "Code"; ws.Cell(row, 3).Value = "Customer Name";
        ws.Cell(row, 4).Value = "Invoices"; ws.Cell(row, 5).Value = "Purchases (USD)";
        ws.Cell(row, 6).Value = "Purchases (ZiG)"; ws.Cell(row, 7).Value = "Payments (USD)";
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
            ws.Cell(row, 4).Value = c.InvoiceCount; ws.Cell(row, 4).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = c.TotalPurchasesUSD; ws.Cell(row, 5).Style.NumberFormat.Format = FormatUsd;
            ws.Cell(row, 6).Value = c.TotalPurchasesZIG; ws.Cell(row, 6).Style.NumberFormat.Format = FormatZig;
            ws.Cell(row, 7).Value = c.TotalPaymentsUSD; ws.Cell(row, 7).Style.NumberFormat.Format = FormatUsd;
            ws.Cell(row, 8).Value = c.OutstandingBalanceUSD; ws.Cell(row, 8).Style.NumberFormat.Format = FormatUsd;
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
        int lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, 8, "No customer purchases were recorded in this period.");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 4, dataStart, lastData, FormatCount);
        ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(ws, row, 5, dataStart, lastData, FormatUsd);
        WriteSubtotal(ws, row, 6, dataStart, lastData, FormatZig);
        WriteSubtotal(ws, row, 7, dataStart, lastData, FormatUsd);
        WriteSubtotal(ws, row, 8, dataStart, lastData, FormatUsd);
        StyleTotalsRow(ws, row, 8);

        WriteFooter(ws, row, 8);
        FinalizeSheet(ws, 8, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // LOW STOCK ALERTS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportLowStockAlertsToExcel(LowStockAlertReport report)
    {
        using var workbook = NewWorkbook("Low Stock Alerts Report");
        var ws = AddSheet(workbook, "Low Stock Alerts", DangerRed);
        int row = WriteReportHeader(ws, "Low Stock Alerts Report", 7, subtitle: $"Report Date: {report.ReportDate:dd MMM yyyy}");

        WriteKpiCard(ws, row, 1, "Total Alerts", report.TotalAlerts, FormatCount);
        WriteKpiCard(ws, row, 2, "Critical", report.CriticalCount, FormatCount, DangerRed);
        WriteKpiCard(ws, row, 3, "Warning", report.WarningCount, FormatCount, WarningOrange);
        row += 3;

        ws.Cell(row, 1).Value = "Alert Level"; ws.Cell(row, 2).Value = "Item Code"; ws.Cell(row, 3).Value = "Item Name";
        ws.Cell(row, 4).Value = "Warehouse"; ws.Cell(row, 5).Value = "Current Stock";
        ws.Cell(row, 6).Value = "Reorder Level"; ws.Cell(row, 7).Value = "Suggested Order";
        StyleTableHeader(ws, row, 7);
        int freezeAt = row;
        row++;
        int dataStart = row;
        // Which rows are critical, so the badge fill can be painted after the row
        // striping rather than under it.
        var criticalRows = new List<int>();
        var warningRows = new List<int>();

        foreach (var item in report.Items.OrderBy(i => i.AlertLevel == "Critical" ? 0 : 1).ThenBy(i => i.CurrentStock))
        {
            ws.Cell(row, 1).Value = item.AlertLevel.ToUpper();
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (item.AlertLevel == "Critical")
            {
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.White;
                criticalRows.Add(row);
            }
            else
            {
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.Black;
                warningRows.Add(row);
            }
            ws.Cell(row, 2).Value = item.ItemCode;
            ws.Cell(row, 3).Value = item.ItemName;
            ws.Cell(row, 4).Value = item.WarehouseCode;
            ws.Cell(row, 5).Value = item.CurrentStock; ws.Cell(row, 5).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 5).Style.Font.Bold = true;
            if (item.CurrentStock <= 0) ws.Cell(row, 5).Style.Font.FontColor = DangerRed;
            else ws.Cell(row, 5).Style.Font.FontColor = WarningOrange;
            ws.Cell(row, 6).Value = item.ReorderLevel; ws.Cell(row, 6).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 7).Value = item.SuggestedReorderQty; ws.Cell(row, 7).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 7).Style.Font.FontColor = XLColor.FromHtml("#1565c0");
            ws.Cell(row, 7).Style.Font.Bold = true;
            row++;
        }
        int lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, 7, "No items are below their reorder level. Nothing to order.");

        foreach (var criticalRow in criticalRows)
            ws.Cell(criticalRow, 1).Style.Fill.BackgroundColor = DangerRed;
        foreach (var warningRow in warningRows)
            ws.Cell(warningRow, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#fff3cd");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 7, dataStart, lastData, FormatCount);
        StyleTotalsRow(ws, row, 7);

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
        var layout = new FulfillmentWorkbookLayout();

        WriteFulfillmentOverviewSheet(workbook, report, layout);
        WriteFulfillmentOrdersSheet(workbook, report);
        WriteFulfillmentLinesSheet(workbook, report);
        WriteFulfillmentCustomerSheet(workbook, report, layout);
        WriteFulfillmentDailySheet(workbook, report, layout);
        WriteFulfillmentNavigation(workbook, layout);
        SetFulfillmentWorkbookProperties(workbook, report);

        workbook.Worksheets.First().SetTabActive();
        return AddFulfillmentCharts(WorkbookToBytes(workbook), layout);
    }

    /// <summary>
    /// Ranges the fulfilment workbook needs after the sheets are written: the navigation strip is
    /// filled once every sheet exists, and the charts are injected into the saved package.
    /// </summary>
    private sealed class FulfillmentWorkbookLayout
    {
        public IXLWorksheet? Overview { get; set; }
        public int NavigationRow { get; set; }
        public int ChartTopRow { get; set; }
        public int ChartBottomRow { get; set; }
        public int CustomerHeaderRow { get; set; }
        public int CustomerFirstRow { get; set; }
        public int CustomerLastRow { get; set; }
        public int DailyHeaderRow { get; set; }
        public int DailyFirstRow { get; set; }
        public int DailyLastRow { get; set; }

        public bool HasCustomerChart => CustomerLastRow >= CustomerFirstRow && CustomerFirstRow > 0;
        public bool HasDailyChart => DailyLastRow >= DailyFirstRow && DailyFirstRow > 0;
    }

    private static void WriteFulfillmentOverviewSheet(XLWorkbook workbook, OrderFulfillmentReport report, FulfillmentWorkbookLayout layout)
    {
        const int lastCol = 12;
        var ws = workbook.Worksheets.Add("Overview");
        ConfigureExecutiveSheet(ws, lastCol, ExecutiveIndigo);

        int row = WriteBrandBanner(
            ws,
            "Sales Order vs Invoice",
            "Order coverage, invoiced value and the pending exposure behind it",
            report.FromDate,
            report.ToDate,
            lastCol,
            ExecutiveRoyalBlue);

        var invoicedRate = report.FulfillmentRatePercent;
        var openShare = CalculateExecutivePercent(report.OpenOrders, report.TotalOrders);
        var lineCoverage = CalculateExecutivePercent(report.FullyDeliveredLines, report.TotalLineItems);
        var pendingShare = CalculateExecutivePercent(report.TotalPendingValueUSD, report.TotalOrderValueUSD);

        WriteExecutiveKpiCard(ws, row, 1, 3, ExecutiveRoyalBlue,
            "ORDERS IN WINDOW", report.TotalOrders.ToString("N0"),
            $"{report.ClosedOrders:N0} closed  |  {report.CancelledOrders:N0} cancelled",
            $"Average order value USD {report.AverageOrderValueUSD:N2}.",
            report.TotalOrders, "#,##0");

        WriteExecutiveKpiCard(ws, row, 4, 6, invoicedRate >= 80m ? ExecutiveEmerald : invoicedRate >= 50m ? ExecutiveAmber : ExecutiveRose,
            "INVOICE RATE", FormatExecutivePercent(invoicedRate),
            $"{report.FullyDeliveredLines:N0} of {report.TotalLineItems:N0} lines fully invoiced",
            $"Line coverage {FormatExecutivePercent(lineCoverage)} across all order lines.",
            invoicedRate / 100m, "0.00%");

        WriteExecutiveKpiCard(ws, row, 7, 9, ExecutiveAmber,
            "OPEN ORDERS", report.OpenOrders.ToString("N0"),
            $"{FormatExecutivePercent(openShare)} of orders raised",
            $"{report.PartiallyDeliveredLines:N0} lines part-invoiced, {report.UndeliveredLines:N0} not started.",
            report.OpenOrders, "#,##0");

        WriteExecutiveKpiCard(ws, row, 10, 12, report.TotalPendingValueUSD > 0 ? ExecutiveRose : ExecutiveEmerald,
            "PENDING VALUE", $"USD {report.TotalPendingValueUSD:N2}",
            $"ZiG {report.TotalPendingValueZIG:N2}",
            $"{FormatExecutivePercent(pendingShare)} of ordered value is still to be invoiced.",
            report.TotalPendingValueUSD, "\"USD \"#,##0.00");

        row += 6;

        // Filled by WriteFulfillmentNavigation once every sheet it links to exists.
        layout.Overview = ws;
        layout.NavigationRow = row;
        row += 2;

        WriteExecutiveCallout(ws, row, lastCol, "WHAT THIS SHOWS",
            $"{report.TotalOrders:N0} sales orders worth USD {report.TotalOrderValueUSD:N2} were raised between " +
            $"{report.FromDate:dd MMM yyyy} and {report.ToDate:dd MMM yyyy}. USD {report.TotalDeliveredValueUSD:N2} has been invoiced " +
            $"and USD {report.TotalPendingValueUSD:N2} remains open across {report.OpenOrders:N0} orders. " +
            "Use Order Details for the order-level position, Item Lines for what is still owed per item, and By Customer for exposure by account.");
        row += 4;

        WriteExecutiveSectionHeader(ws, row, lastCol, "Order pipeline", "Where the orders raised in this window currently sit", ExecutiveRoyalBlue);
        row += 2;

        var pipeline = new (string Label, int Count, XLColor Accent)[]
        {
            ("Closed / fully invoiced", report.ClosedOrders, ExecutiveEmerald),
            ("Open / awaiting invoice", report.OpenOrders, ExecutiveAmber),
            ("Cancelled", report.CancelledOrders, ExecutiveRose)
        };

        ws.Cell(row, 1).Value = "Pipeline stage";
        ws.Cell(row, 5).Value = "Orders";
        ws.Cell(row, 7).Value = "Share";
        ws.Cell(row, 9).Value = "Distribution";
        ws.Range(row, 1, row, 4).Merge();
        ws.Range(row, 5, row, 6).Merge();
        ws.Range(row, 7, row, 8).Merge();
        ws.Range(row, 9, row, lastCol).Merge();
        StyleExecutiveTableHeader(ws, row, lastCol, ExecutiveIndigo);
        row++;

        int pipelineStart = row;
        var pipelineMax = pipeline.Max(p => p.Count);
        foreach (var stage in pipeline)
        {
            ws.Range(row, 1, row, 4).Merge();
            ws.Cell(row, 1).Value = stage.Label;
            ws.Cell(row, 1).Style.Alignment.Indent = 1;

            ws.Range(row, 5, row, 6).Merge();
            ws.Cell(row, 5).Value = stage.Count;
            ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Style.Font.Bold = true;

            ws.Range(row, 7, row, 8).Merge();
            ws.Cell(row, 7).Value = CalculateExecutivePercent(stage.Count, report.TotalOrders) / 100m;
            ws.Cell(row, 7).Style.NumberFormat.Format = "0.0%";
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            ws.Range(row, 9, row, lastCol).Merge();
            ws.Cell(row, 9).Value = BuildExecutiveSignalBar(stage.Count, pipelineMax, 18);
            ws.Cell(row, 9).Style.Font.FontColor = stage.Accent;
            ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            row++;
        }
        StyleExecutiveTableRows(ws, pipelineStart, row - 1, lastCol);
        row += 2;

        WriteExecutiveSectionHeader(ws, row, lastCol, "Value and line coverage", "Ordered against invoiced, in both currencies", ExecutiveCyan);
        row += 2;

        ws.Range(row, 1, row, 6).Merge(); ws.Cell(row, 1).Value = "Measure";
        ws.Range(row, 7, row, 9).Merge(); ws.Cell(row, 7).Value = "USD";
        ws.Range(row, 10, row, lastCol).Merge(); ws.Cell(row, 10).Value = "ZiG";
        StyleExecutiveTableHeader(ws, row, lastCol, ExecutiveIndigo);
        row++;

        int valueStart = row;
        void ValueLine(string label, decimal usd, decimal? zig, bool emphasise = false)
        {
            ws.Range(row, 1, row, 6).Merge();
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Alignment.Indent = 1;
            ws.Cell(row, 1).Style.Font.Bold = emphasise;

            ws.Range(row, 7, row, 9).Merge();
            ws.Cell(row, 7).Value = usd;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, 7).Style.Font.Bold = emphasise;

            ws.Range(row, 10, row, lastCol).Merge();
            if (zig.HasValue)
            {
                ws.Cell(row, 10).Value = zig.Value;
                ws.Cell(row, 10).Style.NumberFormat.Format = "#,##0.00";
            }
            else
            {
                ws.Cell(row, 10).Value = "—";
                ws.Cell(row, 10).Style.Font.FontColor = ExecutiveTextMuted;
            }
            ws.Cell(row, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, 10).Style.Font.Bold = emphasise;
            row++;
        }

        ValueLine("Ordered value", report.TotalOrderValueUSD, report.TotalOrderValueZIG, true);
        ValueLine("Invoiced value", report.TotalDeliveredValueUSD, report.TotalDeliveredValueZIG);
        ValueLine("Pending value", report.TotalPendingValueUSD, report.TotalPendingValueZIG, true);
        ValueLine("Average order value", report.AverageOrderValueUSD, null);
        StyleExecutiveTableRows(ws, valueStart, row - 1, lastCol);

        ws.Cell(valueStart + 2, 7).Style.Font.FontColor = report.TotalPendingValueUSD > 0 ? ExecutiveRose : ExecutiveEmerald;
        ws.Cell(valueStart + 2, 10).Style.Font.FontColor = report.TotalPendingValueZIG > 0 ? ExecutiveRose : ExecutiveEmerald;
        row += 2;

        WriteExecutiveSectionHeader(ws, row, lastCol, "Line status mix", "Every order line raised in the window", ExecutiveEmerald);
        row += 2;

        var lineMix = new (string Label, int Count, XLColor Accent)[]
        {
            ("Fully invoiced lines", report.FullyDeliveredLines, ExecutiveEmerald),
            ("Partially invoiced lines", report.PartiallyDeliveredLines, ExecutiveAmber),
            ("Not yet invoiced lines", report.UndeliveredLines, ExecutiveRose)
        };

        ws.Range(row, 1, row, 6).Merge(); ws.Cell(row, 1).Value = "Line status";
        ws.Range(row, 7, row, 9).Merge(); ws.Cell(row, 7).Value = "Lines";
        ws.Range(row, 10, row, lastCol).Merge(); ws.Cell(row, 10).Value = "Share of lines";
        StyleExecutiveTableHeader(ws, row, lastCol, ExecutiveIndigo);
        row++;

        int mixStart = row;
        foreach (var mix in lineMix)
        {
            ws.Range(row, 1, row, 6).Merge();
            ws.Cell(row, 1).Value = mix.Label;
            ws.Cell(row, 1).Style.Alignment.Indent = 1;

            ws.Range(row, 7, row, 9).Merge();
            ws.Cell(row, 7).Value = mix.Count;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, 7).Style.Font.Bold = true;
            ws.Cell(row, 7).Style.Font.FontColor = mix.Accent;

            ws.Range(row, 10, row, lastCol).Merge();
            ws.Cell(row, 10).Value = CalculateExecutivePercent(mix.Count, report.TotalLineItems) / 100m;
            ws.Cell(row, 10).Style.NumberFormat.Format = "0.0%";
            ws.Cell(row, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            row++;
        }
        StyleExecutiveTableRows(ws, mixStart, row - 1, lastCol);

        ws.Cell(row, 1).Value = $"Total lines: {report.TotalLineItems:N0}";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = ExecutiveTextSecondary;
        row += 3;

        var priorityOrders = report.Orders
            .Select(o => (Order: o, Pending: o.Lines.Sum(CalculatePendingLineValue)))
            .Where(x => x.Pending > 0 && !x.Order.Status.Contains("Cancel", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Pending)
            .Take(5)
            .ToList();

        if (priorityOrders.Count > 0)
        {
            WriteExecutiveSectionHeader(ws, row, lastCol, "Chase list", "The five open orders carrying the largest uninvoiced value", ExecutiveRose);
            row += 2;

            ws.Range(row, 1, row, 2).Merge(); ws.Cell(row, 1).Value = "Order #";
            ws.Range(row, 3, row, 6).Merge(); ws.Cell(row, 3).Value = "Customer";
            ws.Range(row, 7, row, 8).Merge(); ws.Cell(row, 7).Value = "Due";
            ws.Range(row, 9, row, 10).Merge(); ws.Cell(row, 9).Value = "Pending Value";
            ws.Range(row, 11, row, lastCol).Merge(); ws.Cell(row, 11).Value = "Invoice %";
            StyleExecutiveTableHeader(ws, row, lastCol, ExecutiveIndigo);
            row++;

            int priorityStart = row;
            foreach (var (order, pending) in priorityOrders)
            {
                ws.Range(row, 1, row, 2).Merge();
                ws.Cell(row, 1).Value = order.DocNum;
                ws.Cell(row, 1).Style.NumberFormat.Format = "0";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Alignment.Indent = 1;

                ws.Range(row, 3, row, 6).Merge();
                ws.Cell(row, 3).Value = order.CardName;

                ws.Range(row, 7, row, 8).Merge();
                SetExecutiveDateCell(ws.Cell(row, 7), order.DueDate);
                if (order.IsOverdue)
                {
                    ws.Cell(row, 7).Style.Font.Bold = true;
                    ws.Cell(row, 7).Style.Font.FontColor = ExecutiveRose;
                }

                ws.Range(row, 9, row, 10).Merge();
                ws.Cell(row, 9).Value = pending;
                ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(row, 9).Style.Font.Bold = true;

                ws.Range(row, 11, row, lastCol).Merge();
                SetExecutiveCoverageCell(ws.Cell(row, 11), order.FulfillmentPercent);
                row++;
            }
            StyleExecutiveTableRows(ws, priorityStart, row - 1, lastCol, preserveExistingFill: true);
            row += 2;
        }

        if (report.FulfillmentByCustomer.Any() || report.DailyFulfillment.Any())
        {
            WriteExecutiveSectionHeader(ws, row, lastCol, "Charts", "Live Excel charts driven by the detail sheets in this workbook", ExecutiveCyan);
            row += 2;

            bool twoCharts = report.FulfillmentByCustomer.Any() && report.DailyFulfillment.Any();
            layout.ChartTopRow = row;
            layout.ChartBottomRow = row + 17;

            if (report.DailyFulfillment.Any())
            {
                WriteExecutiveChartContainer(ws, row, 1, layout.ChartBottomRow, twoCharts ? 6 : lastCol,
                    "ORDERED VS INVOICED BY DAY",
                    "Quantity raised against quantity invoiced, from the Daily Trend sheet.",
                    ExecutiveRoyalBlue);
            }

            if (report.FulfillmentByCustomer.Any())
            {
                WriteExecutiveChartContainer(ws, row, twoCharts ? 7 : 1, layout.ChartBottomRow, lastCol,
                    "TOP CUSTOMERS: ORDERED VS PENDING",
                    "The ten largest accounts by ordered value, from the By Customer sheet.",
                    ExecutiveRose);
            }

            row = layout.ChartBottomRow + 1;
        }

        WriteExecutiveFooter(ws, row + 1, lastCol);
        FinalizeExecutiveSheet(ws, lastCol, landscape: true);
        ws.Columns(1, lastCol).Width = 11.5;
    }

    private static void WriteFulfillmentOrdersSheet(XLWorkbook workbook, OrderFulfillmentReport report)
    {
        const int lastCol = 13;
        var ws = workbook.Worksheets.Add("Order Details");
        ConfigureExecutiveSheet(ws, lastCol, ExecutiveRoyalBlue);

        int row = WriteBrandBanner(
            ws,
            "Order Details",
            $"{report.Orders.Count:N0} sales orders, newest first, with invoiced and pending quantities",
            report.FromDate,
            report.ToDate,
            lastCol,
            ExecutiveRoyalBlue);

        string[] headers =
        {
            "Order #", "Order Date", "Due Date", "Customer", "Code", "Currency", "Order Total",
            "Status", "Qty Ordered", "Qty Invoiced", "Qty Pending", "Invoice %", "Ageing"
        };
        for (int c = 0; c < headers.Length; c++) ws.Cell(row, c + 1).Value = headers[c];
        StyleExecutiveTableHeader(ws, row, lastCol, ExecutiveIndigo);
        int headerRow = row;
        row++;

        int dataStart = row;
        foreach (var o in report.Orders)
        {
            ws.Cell(row, 1).Value = o.DocNum;
            ws.Cell(row, 1).Style.NumberFormat.Format = "0";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            SetExecutiveDateCell(ws.Cell(row, 2), o.OrderDate);
            SetExecutiveDateCell(ws.Cell(row, 3), o.DueDate);

            ws.Cell(row, 4).Value = o.CardName;
            ws.Cell(row, 5).Value = o.CardCode;
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Style.Font.FontColor = ExecutiveTextSecondary;

            ws.Cell(row, 6).Value = o.DocCurrency;
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            ws.Cell(row, 7).Value = o.OrderTotal;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";

            ws.Cell(row, 8).Value = o.Status;
            ApplyFulfillmentStatusBadge(ws.Cell(row, 8));

            ws.Cell(row, 9).Value = o.TotalQuantityOrdered;
            ws.Cell(row, 10).Value = o.TotalQuantityDelivered;
            ws.Cell(row, 11).Value = o.TotalQuantityPending;
            ws.Range(row, 9, row, 11).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(row, 9, row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            if (o.TotalQuantityPending > 0)
            {
                ws.Cell(row, 11).Style.Font.Bold = true;
                ws.Cell(row, 11).Style.Font.FontColor = ExecutiveAmber;
            }

            SetExecutiveCoverageCell(ws.Cell(row, 12), o.FulfillmentPercent);

            ws.Cell(row, 13).Value = o.IsOverdue ? $"{o.DaysOverdue:N0} days overdue" : "On time";
            ws.Cell(row, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (o.IsOverdue)
            {
                ws.Cell(row, 13).Style.Font.Bold = true;
                ws.Cell(row, 13).Style.Font.FontColor = ExecutiveRose;
            }
            else
            {
                ws.Cell(row, 13).Style.Font.FontColor = ExecutiveTextMuted;
            }

            row++;
        }

        if (row == dataStart)
        {
            WriteExecutiveEmptyState(ws, row, lastCol, "No sales orders fall inside this period.");
            row++;
        }
        else
        {
            StyleExecutiveTableRows(ws, dataStart, row - 1, lastCol, preserveExistingFill: true);
            ws.Range(headerRow, 1, row - 1, lastCol).SetAutoFilter();

            ws.Cell(row, 1).Value = "TOTAL";
            ws.Range(row, 1, row, 6).Merge();
            ws.Cell(row, 7).Value = report.Orders.Sum(o => o.OrderTotal);
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            var totalOrdered = report.Orders.Sum(o => o.TotalQuantityOrdered);
            var totalInvoiced = report.Orders.Sum(o => o.TotalQuantityDelivered);
            ws.Cell(row, 9).Value = totalOrdered;
            ws.Cell(row, 10).Value = totalInvoiced;
            ws.Cell(row, 11).Value = report.Orders.Sum(o => o.TotalQuantityPending);
            ws.Range(row, 9, row, 11).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 12).Value = CalculateExecutivePercent(totalInvoiced, totalOrdered) / 100m;
            ws.Cell(row, 12).Style.NumberFormat.Format = "0.0%";
            StyleExecutiveTotalsRow(ws, row, lastCol);
        }

        WriteExecutiveFooter(ws, row + 2, lastCol);
        FinalizeExecutiveSheet(ws, lastCol, headerRow, 1, landscape: true);
        PadColumnsForAutoFilter(ws, lastCol);
    }

    private static void WriteFulfillmentLinesSheet(XLWorkbook workbook, OrderFulfillmentReport report)
    {
        if (!report.Orders.Any(order => order.Lines.Any())) return;

        const int lastCol = 16;
        var ws = workbook.Worksheets.Add("Item Lines");
        ConfigureExecutiveSheet(ws, lastCol, ExecutiveCyan);

        int lineCount = report.Orders.Sum(o => o.Lines.Count);
        int row = WriteBrandBanner(
            ws,
            "Item Lines",
            $"{lineCount:N0} order lines with the invoice numbers that satisfied them",
            report.FromDate,
            report.ToDate,
            lastCol,
            ExecutiveCyan);

        string[] headers =
        {
            "Order #", "Order Date", "Customer", "Item Code", "Description", "Warehouse", "Line Status",
            "Invoice(s)", "Unit Price", "Qty Ordered", "Qty Invoiced", "Qty Pending", "Invoice %",
            "Ordered Value", "Invoiced Value", "Pending Value"
        };
        for (int c = 0; c < headers.Length; c++) ws.Cell(row, c + 1).Value = headers[c];
        StyleExecutiveTableHeader(ws, row, lastCol, ExecutiveIndigo);
        int headerRow = row;
        row++;

        int dataStart = row;
        decimal orderedValue = 0, invoicedValue = 0, pendingValue = 0;
        foreach (var order in report.Orders)
        {
            foreach (var line in order.Lines)
            {
                var pending = CalculatePendingLineValue(line);
                orderedValue += line.LineTotal;
                invoicedValue += line.InvoicedValue;
                pendingValue += pending;

                ws.Cell(row, 1).Value = order.DocNum;
                ws.Cell(row, 1).Style.NumberFormat.Format = "0";
                ws.Cell(row, 1).Style.Font.Bold = true;
                SetExecutiveDateCell(ws.Cell(row, 2), order.OrderDate);
                ws.Cell(row, 3).Value = order.CardName;
                ws.Cell(row, 4).Value = line.ItemCode;
                ws.Cell(row, 4).Style.Font.Bold = true;
                ws.Cell(row, 5).Value = line.ItemDescription;
                ws.Cell(row, 6).Value = line.WarehouseCode;
                ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws.Cell(row, 7).Value = line.LineStatus;
                ApplyFulfillmentStatusBadge(ws.Cell(row, 7));

                ws.Cell(row, 8).Value = string.IsNullOrWhiteSpace(line.InvoiceNumbers) ? "—" : line.InvoiceNumbers;
                ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                if (string.IsNullOrWhiteSpace(line.InvoiceNumbers)) ws.Cell(row, 8).Style.Font.FontColor = ExecutiveTextMuted;

                ws.Cell(row, 9).Value = line.UnitPrice;
                ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.00";

                ws.Cell(row, 10).Value = line.QuantityOrdered;
                ws.Cell(row, 11).Value = line.QuantityDelivered;
                ws.Cell(row, 12).Value = line.QuantityPending;
                ws.Range(row, 10, row, 12).Style.NumberFormat.Format = "#,##0.00";
                ws.Range(row, 10, row, 12).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                SetExecutiveCoverageCell(
                    ws.Cell(row, 13),
                    line.QuantityOrdered > 0 ? Math.Round(line.QuantityDelivered / line.QuantityOrdered * 100m, 2) : 0m);

                ws.Cell(row, 14).Value = line.LineTotal;
                ws.Cell(row, 15).Value = line.InvoicedValue;
                ws.Cell(row, 16).Value = pending;
                ws.Range(row, 14, row, 16).Style.NumberFormat.Format = "#,##0.00";

                if (line.QuantityPending > 0)
                {
                    ws.Cell(row, 12).Style.Font.Bold = true;
                    ws.Cell(row, 12).Style.Font.FontColor = ExecutiveAmber;
                    ws.Cell(row, 16).Style.Font.Bold = true;
                    ws.Cell(row, 16).Style.Font.FontColor = ExecutiveRose;
                }

                row++;
            }
        }

        StyleExecutiveTableRows(ws, dataStart, row - 1, lastCol, preserveExistingFill: true);
        ws.Range(headerRow, 1, row - 1, lastCol).SetAutoFilter();

        ws.Range(row, 1, row, 9).Merge();
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 14).Value = orderedValue;
        ws.Cell(row, 15).Value = invoicedValue;
        ws.Cell(row, 16).Value = pendingValue;
        ws.Range(row, 14, row, 16).Style.NumberFormat.Format = "#,##0.00";
        StyleExecutiveTotalsRow(ws, row, lastCol);

        WriteExecutiveFooter(ws, row + 2, lastCol);
        FinalizeExecutiveSheet(ws, lastCol, headerRow, 1, landscape: true);
        PadColumnsForAutoFilter(ws, lastCol);
    }

    private static void WriteFulfillmentCustomerSheet(XLWorkbook workbook, OrderFulfillmentReport report, FulfillmentWorkbookLayout layout)
    {
        if (!report.FulfillmentByCustomer.Any()) return;

        const int lastCol = 8;
        var ws = workbook.Worksheets.Add("By Customer");
        ConfigureExecutiveSheet(ws, lastCol, ExecutiveEmerald);

        int row = WriteBrandBanner(
            ws,
            "Invoice Coverage by Customer",
            "Ranked by ordered value, so the largest pending exposure reads first",
            report.FromDate,
            report.ToDate,
            lastCol,
            ExecutiveEmerald);

        string[] headers = { "Customer", "Code", "Orders", "Open", "Closed", "Order Value (USD)", "Invoice %", "Pending Value (USD)" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(row, c + 1).Value = headers[c];
        StyleExecutiveTableHeader(ws, row, lastCol, ExecutiveIndigo);
        int headerRow = row;
        row++;

        int dataStart = row;
        foreach (var c in report.FulfillmentByCustomer.OrderByDescending(x => x.TotalOrderValue))
        {
            ws.Cell(row, 1).Value = c.CardName;
            ws.Cell(row, 2).Value = c.CardCode;
            ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 2).Style.Font.FontColor = ExecutiveTextSecondary;

            ws.Cell(row, 3).Value = c.TotalOrders;
            ws.Cell(row, 4).Value = c.OpenOrders;
            ws.Cell(row, 5).Value = c.ClosedOrders;
            ws.Range(row, 3, row, 5).Style.NumberFormat.Format = "#,##0";
            ws.Range(row, 3, row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (c.OpenOrders > 0) ws.Cell(row, 4).Style.Font.FontColor = ExecutiveAmber;

            ws.Cell(row, 6).Value = c.TotalOrderValue;
            ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";

            SetExecutiveCoverageCell(ws.Cell(row, 7), c.FulfillmentRatePercent);

            ws.Cell(row, 8).Value = c.TotalPendingValue;
            ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
            if (c.TotalPendingValue > 0)
            {
                ws.Cell(row, 8).Style.Font.Bold = true;
                ws.Cell(row, 8).Style.Font.FontColor = ExecutiveRose;
            }
            row++;
        }
        StyleExecutiveTableRows(ws, dataStart, row - 1, lastCol, preserveExistingFill: true);
        ws.Range(headerRow, 1, row - 1, lastCol).SetAutoFilter();

        layout.CustomerHeaderRow = headerRow;
        layout.CustomerFirstRow = dataStart;
        layout.CustomerLastRow = Math.Min(row - 1, dataStart + 9);

        ws.Range(row, 1, row, 2).Merge();
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 3).Value = report.FulfillmentByCustomer.Sum(c => c.TotalOrders);
        ws.Cell(row, 4).Value = report.FulfillmentByCustomer.Sum(c => c.OpenOrders);
        ws.Cell(row, 5).Value = report.FulfillmentByCustomer.Sum(c => c.ClosedOrders);
        ws.Range(row, 3, row, 5).Style.NumberFormat.Format = "#,##0";
        ws.Range(row, 3, row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        var customerOrderValue = report.FulfillmentByCustomer.Sum(c => c.TotalOrderValue);
        var customerPendingValue = report.FulfillmentByCustomer.Sum(c => c.TotalPendingValue);
        ws.Cell(row, 6).Value = customerOrderValue;
        ws.Cell(row, 7).Value = CalculateExecutivePercent(customerOrderValue - customerPendingValue, customerOrderValue) / 100m;
        ws.Cell(row, 7).Style.NumberFormat.Format = "0.0%";
        ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(row, 8).Value = customerPendingValue;
        ws.Range(row, 6, row, 6).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(row, 8, row, 8).Style.NumberFormat.Format = "#,##0.00";
        StyleExecutiveTotalsRow(ws, row, lastCol);

        WriteExecutiveFooter(ws, row + 2, lastCol);
        FinalizeExecutiveSheet(ws, lastCol, headerRow, 1, landscape: true);
        PadColumnsForAutoFilter(ws, lastCol);
    }

    private static void WriteFulfillmentDailySheet(XLWorkbook workbook, OrderFulfillmentReport report, FulfillmentWorkbookLayout layout)
    {
        if (!report.DailyFulfillment.Any()) return;

        const int lastCol = 7;
        var ws = workbook.Worksheets.Add("Daily Trend");
        ConfigureExecutiveSheet(ws, lastCol, ExecutiveAmber);

        int row = WriteBrandBanner(
            ws,
            "Daily Trend",
            "Orders raised and closed each day, with the quantity actually invoiced",
            report.FromDate,
            report.ToDate,
            lastCol,
            ExecutiveAmber);

        string[] headers = { "Date", "Orders Placed", "Orders Closed", "Order Value (USD)", "Qty Ordered", "Qty Invoiced", "Invoice %" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(row, c + 1).Value = headers[c];
        StyleExecutiveTableHeader(ws, row, lastCol, ExecutiveIndigo);
        int headerRow = row;
        row++;

        int dataStart = row;
        foreach (var day in report.DailyFulfillment.OrderBy(d => d.Date))
        {
            ws.Cell(row, 1).Value = day.Date;
            ws.Cell(row, 1).Style.NumberFormat.Format = "ddd, dd MMM yyyy";

            ws.Cell(row, 2).Value = day.OrdersPlaced;
            ws.Cell(row, 3).Value = day.OrdersClosed;
            ws.Range(row, 2, row, 3).Style.NumberFormat.Format = "#,##0";
            ws.Range(row, 2, row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            ws.Cell(row, 4).Value = day.OrderValueUSD;
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";

            ws.Cell(row, 5).Value = day.QuantityOrdered;
            ws.Cell(row, 6).Value = day.QuantityDelivered;
            ws.Range(row, 5, row, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(row, 5, row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            SetExecutiveCoverageCell(
                ws.Cell(row, 7),
                day.QuantityOrdered > 0 ? Math.Round(day.QuantityDelivered / day.QuantityOrdered * 100m, 2) : 0m);
            row++;
        }
        StyleExecutiveTableRows(ws, dataStart, row - 1, lastCol, preserveExistingFill: true);

        layout.DailyHeaderRow = headerRow;
        layout.DailyFirstRow = dataStart;
        layout.DailyLastRow = row - 1;

        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 2).Value = report.DailyFulfillment.Sum(d => d.OrdersPlaced);
        ws.Cell(row, 3).Value = report.DailyFulfillment.Sum(d => d.OrdersClosed);
        ws.Range(row, 2, row, 3).Style.NumberFormat.Format = "#,##0";
        ws.Range(row, 2, row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 4).Value = report.DailyFulfillment.Sum(d => d.OrderValueUSD);
        ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 5).Value = report.DailyFulfillment.Sum(d => d.QuantityOrdered);
        ws.Cell(row, 6).Value = report.DailyFulfillment.Sum(d => d.QuantityDelivered);
        ws.Range(row, 5, row, 6).Style.NumberFormat.Format = "#,##0.00";
        StyleExecutiveTotalsRow(ws, row, lastCol);

        WriteExecutiveFooter(ws, row + 2, lastCol);
        FinalizeExecutiveSheet(ws, lastCol, headerRow);
    }

    /// <summary>
    /// Branded banner used by the executive-styled workbooks that are not tied to a specific report result type.
    /// </summary>
    private static int WriteBrandBanner(
        IXLWorksheet ws,
        string title,
        string subtitle,
        DateTime? fromDate,
        DateTime? toDate,
        int lastCol,
        XLColor accentColor)
    {
        var generatedAt = CurrentCatNow();

        ws.Range(1, 1, 1, lastCol).Style.Fill.BackgroundColor = accentColor;
        ws.Row(1).Height = 6;

        ws.Range(2, 1, 6, lastCol).Style.Fill.BackgroundColor = ExecutiveSurface;
        ws.Range(2, 1, 6, lastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(2, 1, 6, lastCol).Style.Border.OutsideBorderColor = ExecutiveBorder;

        ws.Range(2, 1, 2, lastCol).Merge();
        ws.Cell(2, 1).Value = CompanyName;
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.FontSize = 9;
        ws.Cell(2, 1).Style.Font.FontColor = ExecutiveTextMuted;
        ws.Row(2).Height = 16;

        ws.Range(3, 1, 3, lastCol).Merge();
        ws.Cell(3, 1).Value = title;
        ws.Cell(3, 1).Style.Font.Bold = true;
        ws.Cell(3, 1).Style.Font.FontSize = 20;
        ws.Cell(3, 1).Style.Font.FontColor = ExecutiveTextPrimary;
        ws.Row(3).Height = 28;

        ws.Range(4, 1, 4, lastCol).Merge();
        ws.Cell(4, 1).Value = subtitle;
        ws.Cell(4, 1).Style.Font.FontSize = 10;
        ws.Cell(4, 1).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Row(4).Height = 18;

        var period = fromDate.HasValue && toDate.HasValue
            ? $"Period {fromDate.Value:dd MMM yyyy} to {toDate.Value:dd MMM yyyy}"
            : "All dates";

        ws.Range(5, 1, 5, lastCol).Merge();
        ws.Cell(5, 1).Value = $"{period}  |  {SystemName}  |  Generated {generatedAt:dd MMM yyyy HH:mm} CAT";
        ws.Cell(5, 1).Style.Font.FontSize = 9;
        ws.Cell(5, 1).Style.Font.FontColor = ExecutiveTextMuted;
        ws.Row(5).Height = 16;

        ws.Range(6, 1, 6, lastCol).Style.Fill.BackgroundColor = ExecutiveSection;
        ws.Row(6).Height = 4;

        return 8;
    }

    /// <summary>
    /// Fills the Overview navigation strip. Runs after every sheet exists so each link has a target.
    /// </summary>
    private static void WriteFulfillmentNavigation(XLWorkbook workbook, FulfillmentWorkbookLayout layout)
    {
        var ws = layout.Overview;
        if (ws is null || layout.NavigationRow <= 0) return;

        const int lastCol = 12;
        int row = layout.NavigationRow;

        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Value = "IN THIS WORKBOOK";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.FontColor = ExecutiveTextMuted;
        row++;

        var targets = workbook.Worksheets
            .Where(sheet => !string.Equals(sheet.Name, ws.Name, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();

        int width = Math.Max(1, lastCol / Math.Max(1, targets.Count));
        for (int index = 0; index < targets.Count; index++)
        {
            int startCol = (index * width) + 1;
            int endCol = index == targets.Count - 1 ? lastCol : startCol + width - 1;

            ws.Range(row, startCol, row, endCol).Merge();
            var cell = ws.Cell(row, startCol);
            cell.Value = $"→  {targets[index].Name}";
            cell.SetHyperlink(new XLHyperlink(targets[index].Cell(1, 1)));
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 10;
            cell.Style.Font.FontColor = ExecutiveRoyalBlue;
            cell.Style.Font.Underline = XLFontUnderlineValues.None;
            cell.Style.Fill.BackgroundColor = ExecutiveSoftBlue;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = ExecutiveBorder;
        }
        ws.Row(row).Height = 20;
    }

    private static void SetFulfillmentWorkbookProperties(XLWorkbook workbook, OrderFulfillmentReport report)
    {
        workbook.Properties.Title = "Sales Order vs Invoice";
        workbook.Properties.Subject = $"Order coverage {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}";
        workbook.Properties.Company = CompanyName;
        workbook.Properties.Author = SystemName;
        workbook.Properties.Category = "Sales reporting";
        workbook.Properties.Keywords = "sales orders, invoices, fulfilment, pending value";
        workbook.Properties.Comments = "Confidential management report generated by the Shop Inventory Management System.";
    }

    // ApplyFulfillmentPrintSetup lived here. Both branches reached for the same page
    // footer independently, and FinalizeExecutiveSheet now repeats the header row and
    // stamps company/page-number text for every executive sheet. Keeping both would not
    // have been harmless: ClosedXML's AddText appends to the header/footer item, so the
    // five fulfilment sheets would have printed "Page 1 of 3Page 1 of 3".

    /// <summary>
    /// Injects the native Excel charts that the Overview sheet reserved space for.
    /// </summary>
    private static byte[] AddFulfillmentCharts(byte[] workbookBytes, FulfillmentWorkbookLayout layout)
    {
        if (layout.ChartTopRow <= 0 || (!layout.HasDailyChart && !layout.HasCustomerChart))
        {
            return workbookBytes;
        }

        bool twoCharts = layout.HasDailyChart && layout.HasCustomerChart;

        // The container reserves two rows for its title and caption; the chart sits below them.
        int fromRow = layout.ChartTopRow + 1;
        int toRow = layout.ChartBottomRow;

        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            if (layout.HasDailyChart)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Overview",
                    chartName: "Ordered vs invoiced by day",
                    sourceSheetName: "Daily Trend",
                    headerRow: layout.DailyHeaderRow,
                    categoryColumn: 1,
                    dataStartRow: layout.DailyFirstRow,
                    dataEndRow: layout.DailyLastRow,
                    seriesColumns: new[] { 5, 6 },
                    seriesColors: new[] { "2563EB", "10B981" },
                    fromColumn: 0,
                    fromRow: fromRow,
                    toColumn: twoCharts ? 6 : 12,
                    toRow: toRow);
            }

            if (layout.HasCustomerChart)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Overview",
                    chartName: "Top customers ordered vs pending",
                    sourceSheetName: "By Customer",
                    headerRow: layout.CustomerHeaderRow,
                    categoryColumn: 1,
                    dataStartRow: layout.CustomerFirstRow,
                    dataEndRow: layout.CustomerLastRow,
                    seriesColumns: new[] { 6, 8 },
                    seriesColors: new[] { "2563EB", "F43F5E" },
                    fromColumn: twoCharts ? 6 : 0,
                    fromRow: fromRow,
                    toColumn: 12,
                    toRow: toRow);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Auto-fit leaves no room for the AutoFilter arrow, which then covers the header caption.
    /// </summary>
    private static void PadColumnsForAutoFilter(IXLWorksheet ws, int lastCol)
    {
        for (var col = 1; col <= lastCol; col++)
        {
            ws.Column(col).Width = Math.Min(38, ws.Column(col).Width + 3);
        }
    }

    private static void SetExecutiveDateCell(IXLCell cell, DateTime value)
    {
        cell.Value = value.Date;
        cell.Style.NumberFormat.Format = "dd MMM yyyy";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    /// <summary>
    /// Writes an invoice-coverage percentage with a traffic-light band.
    /// </summary>
    private static void SetExecutiveCoverageCell(IXLCell cell, decimal percentValue)
    {
        cell.Value = percentValue / 100m;
        cell.Style.NumberFormat.Format = "0.0%";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Font.Bold = true;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = ExecutiveBorder;

        if (percentValue >= 99.5m)
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftEmerald;
            cell.Style.Font.FontColor = ExecutiveEmerald;
        }
        else if (percentValue >= 50m)
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

    /// <summary>
    /// Order and line status pill colouring for the fulfilment workbook.
    /// </summary>
    private static void ApplyFulfillmentStatusBadge(IXLCell cell)
    {
        var status = cell.GetString().Trim().ToUpperInvariant();

        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 9;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = ExecutiveBorder;

        if (status.Contains("CANCEL"))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftRose;
            cell.Style.Font.FontColor = ExecutiveRose;
        }
        else if (status.Contains("CLOSED") || status.Contains("FULLY") || status.Contains("INVOICED") && !status.Contains("NOT") && !status.Contains("PART"))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftEmerald;
            cell.Style.Font.FontColor = ExecutiveEmerald;
        }
        else if (status.Contains("PART"))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftAmber;
            cell.Style.Font.FontColor = ExecutiveAmber;
        }
        else if (status.Contains("OPEN") || status.Contains("PENDING") || status.Contains("NOT"))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftBlue;
            cell.Style.Font.FontColor = ExecutiveRoyalBlue;
        }
        else
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftIndigo;
            cell.Style.Font.FontColor = ExecutiveIndigo;
        }
    }

    private static void StyleExecutiveTotalsRow(IXLWorksheet ws, int row, int lastCol)
    {
        var range = ws.Range(row, 1, row, lastCol);
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 10;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = ExecutiveIndigo;
        range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        range.Style.Border.TopBorderColor = ExecutiveIndigo;
        ws.Row(row).Height = 22;
        ws.Cell(row, 1).Style.Alignment.Indent = 1;
    }

    private static void WriteExecutiveEmptyState(IXLWorksheet ws, int row, int lastCol, string message)
    {
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Value = message;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 1).Style.Fill.BackgroundColor = ExecutiveSurface;
        ws.Cell(row, 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Cell(row, 1).Style.Border.OutsideBorderColor = ExecutiveBorder;
        ws.Row(row).Height = 28;
    }

    // ═══════════════════════════════════════════════════════════════
    // CREDIT NOTES
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportCreditNoteSummaryToExcel(CreditNoteSummaryReport report)
    {
        using var workbook = NewWorkbook("Credit Notes Summary Report");

        var dash = AddSheet(workbook, "Credit Notes Dashboard");
        int row = WriteReportHeader(dash, "Credit Notes Summary Report", 5, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total Credit Notes", report.TotalCreditNotes, FormatCount);
        WriteKpiCard(dash, row, 2, "Total (USD)", report.TotalCreditAmountUSD, FormatUsd, DangerRed);
        WriteKpiCard(dash, row, 3, "Total (ZiG)", report.TotalCreditAmountZIG, FormatZig);
        WriteKpiCard(dash, row, 4, "Avg Value (USD)", report.AverageCreditNoteValueUSD, FormatUsd);
        WriteKpiCard(dash, row, 5, "Credit-to-Sales", report.CreditToSalesRatioPercent / 100, FormatPercent, report.CreditToSalesRatioPercent > 5 ? DangerRed : SuccessGreen);
        row += 3;

        WriteKpiRow(dash, row, "Total Credit Notes", report.TotalCreditNotes, FormatCount); row++;
        WriteKpiRow(dash, row, "Total Amount (USD)", report.TotalCreditAmountUSD, FormatUsd, true); row++;
        WriteKpiRow(dash, row, "Total Amount (ZiG)", report.TotalCreditAmountZIG, FormatZig); row++;
        WriteKpiRow(dash, row, "VAT (USD)", report.TotalVatUSD, FormatUsd); row++;
        WriteKpiRow(dash, row, "Avg Credit Note (USD)", report.AverageCreditNoteValueUSD, FormatUsd); row++;
        WriteKpiRow(dash, row, "Unique Customers", report.UniqueCustomers, FormatCount); row++;
        WriteKpiRow(dash, row, "Credit-to-Sales Ratio", report.CreditToSalesRatioPercent / 100, FormatPercent, true); row++;

        WriteFooter(dash, row, 5);
        FinalizeSheet(dash, 5);

        var cws = AddSheet(workbook, "By Customer");
        int cRow = WriteReportHeader(cws, "Credit Notes by Customer", 5, report.FromDate, report.ToDate);

        cws.Cell(cRow, 1).Value = "Customer Code"; cws.Cell(cRow, 2).Value = "Customer Name"; cws.Cell(cRow, 3).Value = "Count";
        cws.Cell(cRow, 4).Value = "Amount (USD)"; cws.Cell(cRow, 5).Value = "Amount (ZiG)";
        StyleTableHeader(cws, cRow, 5);
        int cFreeze = cRow;
        cRow++;
        int cStart = cRow;
        foreach (var c in report.ByCustomer.OrderByDescending(x => x.TotalAmountUSD))
        {
            cws.Cell(cRow, 1).Value = c.CardCode;
            cws.Cell(cRow, 2).Value = c.CardName;
            cws.Cell(cRow, 3).Value = c.CreditNoteCount; cws.Cell(cRow, 3).Style.NumberFormat.Format = FormatCount;
            cws.Cell(cRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cws.Cell(cRow, 4).Value = c.TotalAmountUSD; cws.Cell(cRow, 4).Style.NumberFormat.Format = FormatUsd;
            cws.Cell(cRow, 5).Value = c.TotalAmountZIG; cws.Cell(cRow, 5).Style.NumberFormat.Format = FormatZig;
            cRow++;
        }
        int lastCustomer = cRow - 1;
        cRow = FinishTable(cws, cFreeze, cStart, cRow, 5, "No credit notes were raised in this period.");

        cws.Cell(cRow, 1).Value = "TOTAL";
        WriteSubtotal(cws, cRow, 3, cStart, lastCustomer, FormatCount);
        cws.Cell(cRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(cws, cRow, 4, cStart, lastCustomer, FormatUsd);
        WriteSubtotal(cws, cRow, 5, cStart, lastCustomer, FormatZig);
        StyleTotalsRow(cws, cRow, 5);

        WriteFooter(cws, cRow, 5);
        FinalizeSheet(cws, 5, cFreeze);

        var pws = AddSheet(workbook, "Products Returned");
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
            pws.Cell(pRow, 3).Value = p.TotalQuantityReturned; pws.Cell(pRow, 3).Style.NumberFormat.Format = FormatCount;
            pws.Cell(pRow, 4).Value = p.TotalCreditAmountUSD; pws.Cell(pRow, 4).Style.NumberFormat.Format = FormatUsd;
            pws.Cell(pRow, 5).Value = p.TimesReturned; pws.Cell(pRow, 5).Style.NumberFormat.Format = FormatCount;
            pws.Cell(pRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            pRow++;
        }
        int lastProduct = pRow - 1;
        pRow = FinishTable(pws, pFreeze, pStart, pRow, 5, "No products were returned in this period.");

        pws.Cell(pRow, 1).Value = "TOTAL";
        WriteSubtotal(pws, pRow, 3, pStart, lastProduct, FormatCount);
        WriteSubtotal(pws, pRow, 4, pStart, lastProduct, FormatUsd);
        WriteSubtotal(pws, pRow, 5, pStart, lastProduct, FormatCount);
        pws.Cell(pRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(pws, pRow, 5);

        WriteFooter(pws, pRow, 5);
        FinalizeSheet(pws, 5, pFreeze);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // PURCHASE ORDERS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportPurchaseOrderSummaryToExcel(PurchaseOrderSummaryReport report)
    {
        using var workbook = NewWorkbook("Purchase Orders Summary Report");

        var dash = AddSheet(workbook, "Purchasing Dashboard");
        int row = WriteReportHeader(dash, "Purchase Orders Summary Report", 5, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Total POs", report.TotalPurchaseOrders, FormatCount);
        WriteKpiCard(dash, row, 2, "Total Value (USD)", report.TotalOrderValueUSD, FormatUsd);
        WriteKpiCard(dash, row, 3, "Open POs", report.OpenOrders, FormatCount, WarningOrange);
        WriteKpiCard(dash, row, 4, "Pending Value (USD)", report.TotalPendingValueUSD, FormatUsd, DangerRed);
        WriteKpiCard(dash, row, 5, "Unique Suppliers", report.UniqueSuppliers, FormatCount);
        row += 3;

        WriteKpiRow(dash, row, "Total Purchase Orders", report.TotalPurchaseOrders, FormatCount); row++;
        WriteKpiRow(dash, row, "Open Orders", report.OpenOrders, FormatCount); row++;
        WriteKpiRow(dash, row, "Closed Orders", report.ClosedOrders, FormatCount); row++;
        WriteKpiRow(dash, row, "Cancelled Orders", report.CancelledOrders, FormatCount); row++;
        WriteKpiRow(dash, row, "Total Value (USD)", report.TotalOrderValueUSD, FormatUsd, true); row++;
        WriteKpiRow(dash, row, "Total Value (ZiG)", report.TotalOrderValueZIG, FormatZig); row++;
        WriteKpiRow(dash, row, "Pending Value (USD)", report.TotalPendingValueUSD, FormatUsd); row++;
        WriteKpiRow(dash, row, "Avg Order Value (USD)", report.AverageOrderValueUSD, FormatUsd); row++;

        WriteFooter(dash, row, 5);
        FinalizeSheet(dash, 5);

        var sws = AddSheet(workbook, "By Supplier");
        int sRow = WriteReportHeader(sws, "Purchase Orders by Supplier", 7, report.FromDate, report.ToDate);

        sws.Cell(sRow, 1).Value = "Supplier Code"; sws.Cell(sRow, 2).Value = "Supplier Name"; sws.Cell(sRow, 3).Value = "POs";
        sws.Cell(sRow, 4).Value = "Total (USD)"; sws.Cell(sRow, 5).Value = "Total (ZiG)";
        sws.Cell(sRow, 6).Value = "Open POs"; sws.Cell(sRow, 7).Value = "Pending (USD)";
        StyleTableHeader(sws, sRow, 7);
        int sFreeze = sRow;
        sRow++;
        int sStart = sRow;
        foreach (var s in report.BySupplier.OrderByDescending(x => x.TotalValueUSD))
        {
            sws.Cell(sRow, 1).Value = s.CardCode;
            sws.Cell(sRow, 2).Value = s.CardName;
            sws.Cell(sRow, 3).Value = s.OrderCount; sws.Cell(sRow, 3).Style.NumberFormat.Format = FormatCount;
            sws.Cell(sRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sws.Cell(sRow, 4).Value = s.TotalValueUSD; sws.Cell(sRow, 4).Style.NumberFormat.Format = FormatUsd;
            sws.Cell(sRow, 5).Value = s.TotalValueZIG; sws.Cell(sRow, 5).Style.NumberFormat.Format = FormatZig;
            sws.Cell(sRow, 6).Value = s.OpenOrders; sws.Cell(sRow, 6).Style.NumberFormat.Format = FormatCount;
            sws.Cell(sRow, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sws.Cell(sRow, 7).Value = s.PendingValueUSD; sws.Cell(sRow, 7).Style.NumberFormat.Format = FormatUsd;
            if (s.PendingValueUSD > 0) sws.Cell(sRow, 7).Style.Font.FontColor = DangerRed;
            sRow++;
        }
        int lastSupplier = sRow - 1;
        sRow = FinishTable(sws, sFreeze, sStart, sRow, 7, "No purchase orders were raised in this period.");

        sws.Cell(sRow, 1).Value = "TOTAL";
        WriteSubtotal(sws, sRow, 3, sStart, lastSupplier, FormatCount);
        sws.Cell(sRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(sws, sRow, 4, sStart, lastSupplier, FormatUsd);
        WriteSubtotal(sws, sRow, 5, sStart, lastSupplier, FormatZig);
        WriteSubtotal(sws, sRow, 6, sStart, lastSupplier, FormatCount);
        sws.Cell(sRow, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(sws, sRow, 7, sStart, lastSupplier, FormatUsd);
        StyleTotalsRow(sws, sRow, 7);

        WriteFooter(sws, sRow, 7);
        FinalizeSheet(sws, 7, sFreeze, landscape: true);

        var pws = AddSheet(workbook, "Top Products");
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
            pws.Cell(pRow, 3).Value = p.TotalQuantityOrdered; pws.Cell(pRow, 3).Style.NumberFormat.Format = FormatCount;
            pws.Cell(pRow, 4).Value = p.TotalCostUSD; pws.Cell(pRow, 4).Style.NumberFormat.Format = FormatUsd;
            pws.Cell(pRow, 5).Value = p.TimesOrdered; pws.Cell(pRow, 5).Style.NumberFormat.Format = FormatCount;
            pws.Cell(pRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            pRow++;
        }
        int lastProduct = pRow - 1;
        pRow = FinishTable(pws, pFreeze, pStart, pRow, 5, "No products were purchased in this period.");

        pws.Cell(pRow, 1).Value = "TOTAL";
        WriteSubtotal(pws, pRow, 3, pStart, lastProduct, FormatCount);
        WriteSubtotal(pws, pRow, 4, pStart, lastProduct, FormatUsd);
        WriteSubtotal(pws, pRow, 5, pStart, lastProduct, FormatCount);
        pws.Cell(pRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(pws, pRow, 5);

        WriteFooter(pws, pRow, 5);
        FinalizeSheet(pws, 5, pFreeze);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // RECEIVABLES AGING
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportReceivablesAgingToExcel(ReceivablesAgingReport report)
    {
        using var workbook = NewWorkbook("Receivables Aging Report");

        var ws = AddSheet(workbook, "Aging Summary");
        int row = WriteReportHeader(ws, "Receivables Aging Report", 5, subtitle: $"Report Date: {report.ReportDate:dd MMM yyyy}");

        WriteKpiCard(ws, row, 1, "Total Outstanding (USD)", report.TotalOutstandingUSD, FormatUsd, DangerRed);
        WriteKpiCard(ws, row, 2, "Outstanding (ZiG)", report.TotalOutstandingZIG, FormatZig);
        WriteKpiCard(ws, row, 3, "Total Customers", report.TotalCustomers, FormatCount);
        WriteKpiCard(ws, row, 4, "Current (0-30d)", report.Current.AmountUSD, FormatUsd, SuccessGreen);
        WriteKpiCard(ws, row, 5, "Over 90 days", report.Over90Days.AmountUSD, FormatUsd, DangerRed);
        row += 3;

        ws.Cell(row, 1).Value = "Aging Bucket"; ws.Cell(row, 2).Value = "Invoices";
        ws.Cell(row, 3).Value = "Amount (USD)"; ws.Cell(row, 4).Value = "Amount (ZiG)"; ws.Cell(row, 5).Value = "% of Total";
        StyleTableHeader(ws, row, 5);
        int bucketHeader = row;
        row++;
        int dataStart = row;

        void WriteBucket(AgingBucket bucket, string label, XLColor? color = null)
        {
            ws.Cell(row, 1).Value = label;
            if (color != null) { ws.Cell(row, 1).Style.Font.FontColor = color; ws.Cell(row, 1).Style.Font.Bold = true; }
            ws.Cell(row, 2).Value = bucket.InvoiceCount; ws.Cell(row, 2).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 3).Value = bucket.AmountUSD; ws.Cell(row, 3).Style.NumberFormat.Format = FormatUsd;
            ws.Cell(row, 4).Value = bucket.AmountZIG; ws.Cell(row, 4).Style.NumberFormat.Format = FormatZig;
            ws.Cell(row, 5).Value = bucket.PercentOfTotal / 100; ws.Cell(row, 5).Style.NumberFormat.Format = FormatPercent;
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;
        }

        WriteBucket(report.Current, "Current (0\u201330 days)", SuccessGreen);
        WriteBucket(report.Days31To60, "31\u201360 days", WarningOrange);
        WriteBucket(report.Days61To90, "61\u201390 days", WarningOrange);
        WriteBucket(report.Over90Days, "Over 90 days", DangerRed);
        int lastBucket = row - 1;
        // Four fixed buckets in a deliberate order: filtering them would only hide one.
        row = FinishTable(ws, bucketHeader, dataStart, row, 5, filter: false);

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 2, dataStart, lastBucket, FormatCount);
        ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(ws, row, 3, dataStart, lastBucket, FormatUsd);
        WriteSubtotal(ws, row, 4, dataStart, lastBucket, FormatZig);
        WriteSubtotal(ws, row, 5, dataStart, lastBucket, FormatPercent);
        ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(ws, row, 5);

        WriteFooter(ws, row, 5);
        FinalizeSheet(ws, 5, bucketHeader);

        var cws = AddSheet(workbook, "Customer Aging Detail");
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
            cws.Cell(cRow, 3).Value = c.TotalOutstandingUSD; cws.Cell(cRow, 3).Style.NumberFormat.Format = FormatUsd;
            cws.Cell(cRow, 3).Style.Font.Bold = true;
            cws.Cell(cRow, 4).Value = c.CurrentUSD; cws.Cell(cRow, 4).Style.NumberFormat.Format = FormatUsd;
            cws.Cell(cRow, 5).Value = c.Days31To60USD; cws.Cell(cRow, 5).Style.NumberFormat.Format = FormatUsd;
            if (c.Days31To60USD > 0) cws.Cell(cRow, 5).Style.Font.FontColor = WarningOrange;
            cws.Cell(cRow, 6).Value = c.Days61To90USD; cws.Cell(cRow, 6).Style.NumberFormat.Format = FormatUsd;
            if (c.Days61To90USD > 0) cws.Cell(cRow, 6).Style.Font.FontColor = WarningOrange;
            cws.Cell(cRow, 7).Value = c.Over90DaysUSD; cws.Cell(cRow, 7).Style.NumberFormat.Format = FormatUsd;
            if (c.Over90DaysUSD > 0) { cws.Cell(cRow, 7).Style.Font.FontColor = DangerRed; cws.Cell(cRow, 7).Style.Font.Bold = true; }
            cws.Cell(cRow, 8).Value = c.TotalInvoices; cws.Cell(cRow, 8).Style.NumberFormat.Format = FormatCount;
            cws.Cell(cRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cRow++;
        }
        int lastAging = cRow - 1;
        cRow = FinishTable(cws, cFreeze, cStart, cRow, 8, "No customer has an outstanding balance.");

        cws.Cell(cRow, 1).Value = "TOTAL";
        for (int col = 3; col <= 7; col++)
        {
            WriteSubtotal(cws, cRow, col, cStart, lastAging, FormatUsd);
        }
        WriteSubtotal(cws, cRow, 8, cStart, lastAging, FormatCount);
        cws.Cell(cRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(cws, cRow, 8);

        WriteFooter(cws, cRow, 8);
        FinalizeSheet(cws, 8, cFreeze, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // PROFIT OVERVIEW
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportProfitOverviewToExcel(ProfitOverviewReport report)
    {
        using var workbook = NewWorkbook("Profit & Loss Overview");

        var dash = AddSheet(workbook, "Profit & Loss");
        int row = WriteReportHeader(dash, "Profit & Loss Overview", 4, report.FromDate, report.ToDate);

        WriteKpiCard(dash, row, 1, "Net Revenue (USD)", report.NetRevenueUSD, FormatUsd);
        WriteKpiCard(dash, row, 2, "Gross Profit (USD)", report.GrossProfitUSD, FormatUsd, report.GrossProfitUSD >= 0 ? SuccessGreen : DangerRed);
        WriteKpiCard(dash, row, 3, "Gross Margin", report.GrossMarginPercent / 100, FormatPercent, report.GrossMarginPercent >= 20 ? SuccessGreen : DangerRed);
        WriteKpiCard(dash, row, 4, "Collection Rate", report.CollectionRatePercent / 100, FormatPercent);
        row += 3;

        WriteSectionTitle(dash, row, 4, "INCOME STATEMENT");
        row++;

        dash.Cell(row, 1).Value = ""; dash.Cell(row, 2).Value = "USD"; dash.Cell(row, 3).Value = "ZiG"; dash.Cell(row, 4).Value = "Notes";
        StyleTableHeader(dash, row, 4);
        int statementHeader = row;
        row++;
        int statementStart = row;

        void PLRow(string label, decimal usd, decimal zig, string notes = "", bool bold = false, XLColor? color = null)
        {
            dash.Cell(row, 1).Value = label;
            dash.Cell(row, 1).Style.Font.Bold = bold;
            if (!bold) dash.Cell(row, 1).Style.Alignment.Indent = 1;

            dash.Cell(row, 2).Value = usd; dash.Cell(row, 2).Style.NumberFormat.Format = FormatUsd;
            dash.Cell(row, 3).Value = zig; dash.Cell(row, 3).Style.NumberFormat.Format = FormatZig;
            dash.Cell(row, 4).Value = notes;
            dash.Cell(row, 4).Style.Font.FontSize = 9;
            dash.Cell(row, 4).Style.Font.FontColor = MutedText;

            if (bold) { dash.Cell(row, 2).Style.Font.Bold = true; dash.Cell(row, 3).Style.Font.Bold = true; }
            if (color != null) dash.Cell(row, 2).Style.Font.FontColor = color;
            row++;
        }

        PLRow("Gross Sales", report.TotalRevenueUSD, report.TotalRevenueZIG, $"{report.TotalInvoices:N0} invoices");
        PLRow("Less: Credit Notes", report.TotalCreditNotesUSD, report.TotalCreditNotesZIG, $"{report.TotalCreditNoteCount:N0} credit notes", color: DangerRed);

        // Net Revenue highlight
        var netRevenueRow = row;
        dash.Cell(row, 1).Value = "NET REVENUE";
        dash.Cell(row, 2).Value = report.NetRevenueUSD; dash.Cell(row, 2).Style.NumberFormat.Format = FormatUsd;
        dash.Cell(row, 3).Value = report.NetRevenueZIG; dash.Cell(row, 3).Style.NumberFormat.Format = FormatZig;
        row++;

        PLRow("Less: Purchases (COGS)", report.TotalPurchaseCostUSD, report.TotalPurchaseCostZIG, "Cost of goods sold", color: DangerRed);

        // Gross Profit highlight
        var grossProfitRow = row;
        dash.Cell(row, 1).Value = "GROSS PROFIT";
        dash.Cell(row, 2).Value = report.GrossProfitUSD; dash.Cell(row, 2).Style.NumberFormat.Format = FormatUsd;
        dash.Cell(row, 3).Value = report.GrossProfitZIG; dash.Cell(row, 3).Style.NumberFormat.Format = FormatZig;
        dash.Cell(row, 4).Value = $"Margin: {report.GrossMarginPercent:N1}%";
        row++;

        PLRow("Payments Received", report.TotalCollectedUSD, report.TotalCollectedZIG, $"{report.TotalPayments:N0} payments");
        PLRow("Outstanding Receivables", report.OutstandingReceivablesUSD, report.OutstandingReceivablesZIG, $"Collection Rate: {report.CollectionRatePercent:N1}%", color: DangerRed);

        // A statement, not a list: the ordering is the meaning, so no filter and no
        // totals row underneath the subtotals it already carries.
        StyleDataRows(dash, statementStart, row - 1, 4);
        dash.Range(statementStart, 2, row - 1, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        // The two subtotal bands are painted after the striping, which would otherwise
        // have laid its alternating fill straight over them.
        dash.Range(netRevenueRow, 1, netRevenueRow, 4).Style.Font.Bold = true;
        dash.Range(netRevenueRow, 1, netRevenueRow, 4).Style.Fill.BackgroundColor = AccentBlue;
        dash.Range(grossProfitRow, 1, grossProfitRow, 4).Style.Font.Bold = true;
        dash.Range(grossProfitRow, 1, grossProfitRow, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#e8f5e9");
        dash.Cell(grossProfitRow, 2).Style.Font.FontColor = report.GrossProfitUSD >= 0 ? SuccessGreen : DangerRed;
        row++;

        WriteSectionTitle(dash, row, 4, "OPERATING METRICS");
        row++;
        WriteKpiRow(dash, row, "Total Invoices", report.TotalInvoices, FormatCount); row++;
        WriteKpiRow(dash, row, "Total Credit Notes", report.TotalCreditNoteCount, FormatCount); row++;
        WriteKpiRow(dash, row, "Total Payments", report.TotalPayments, FormatCount); row++;
        WriteKpiRow(dash, row, "Unique Customers", report.UniqueCustomers, FormatCount); row++;
        WriteKpiRow(dash, row, "Gross Margin %", report.GrossMarginPercent / 100, FormatPercent, true); row++;
        WriteKpiRow(dash, row, "Collection Rate %", report.CollectionRatePercent / 100, FormatPercent, true); row++;

        // Fit the columns to the income statement, but do not freeze it: the whole
        // sheet is a summary that reads top to bottom, so there is nothing to scroll
        // a heading away from.
        WriteFooter(dash, row, 4);
        FinalizeSheet(dash, 4, fitFromRow: statementHeader);

        var mws = AddSheet(workbook, "Monthly Breakdown");
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
            mws.Cell(mRow, 2).Value = m.RevenueUSD; mws.Cell(mRow, 2).Style.NumberFormat.Format = FormatUsd;
            mws.Cell(mRow, 3).Value = m.CreditNotesUSD; mws.Cell(mRow, 3).Style.NumberFormat.Format = FormatUsd;
            if (m.CreditNotesUSD > 0) mws.Cell(mRow, 3).Style.Font.FontColor = DangerRed;
            mws.Cell(mRow, 4).Value = net; mws.Cell(mRow, 4).Style.NumberFormat.Format = FormatUsd;
            mws.Cell(mRow, 5).Value = m.PurchaseCostUSD; mws.Cell(mRow, 5).Style.NumberFormat.Format = FormatUsd;
            mws.Cell(mRow, 6).Value = gp; mws.Cell(mRow, 6).Style.NumberFormat.Format = FormatUsd;
            mws.Cell(mRow, 6).Style.Font.FontColor = gp >= 0 ? SuccessGreen : DangerRed;
            mws.Cell(mRow, 6).Style.Font.Bold = true;
            mws.Cell(mRow, 7).Value = margin / 100; mws.Cell(mRow, 7).Style.NumberFormat.Format = FormatPercent;
            mws.Cell(mRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            mws.Cell(mRow, 8).Value = m.InvoiceCount; mws.Cell(mRow, 8).Style.NumberFormat.Format = FormatCount;
            mws.Cell(mRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            mRow++;
        }
        int lastMonth = mRow - 1;
        mRow = FinishTable(mws, mFreeze, mStart, mRow, 8, "No trading months fell in this period.");

        mws.Cell(mRow, 1).Value = "TOTAL";
        for (int col = 2; col <= 6; col++)
        {
            WriteSubtotal(mws, mRow, col, mStart, lastMonth, FormatUsd);
        }
        // Margin is a ratio of the two subtotals above it, not a sum of the monthly
        // margins, so it is derived rather than SUBTOTALled — and it still follows the
        // filter, because the cells it divides do.
        var netCell = $"D{mRow}";
        var profitCell = $"F{mRow}";
        mws.Cell(mRow, 7).FormulaA1 = $"IF({netCell}=0,0,{profitCell}/{netCell})";
        mws.Cell(mRow, 7).Style.NumberFormat.Format = FormatPercent;
        mws.Cell(mRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(mws, mRow, 8, mStart, lastMonth, FormatCount);
        mws.Cell(mRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(mws, mRow, 8);

        WriteFooter(mws, mRow, 8);
        FinalizeSheet(mws, 8, mFreeze, landscape: true);

        return WorkbookToBytes(workbook);
    }

    // ═══════════════════════════════════════════════════════════════
    // SLOW MOVING PRODUCTS
    // ═══════════════════════════════════════════════════════════════
    public byte[] ExportSlowMovingProductsToExcel(SlowMovingProductsReport report)
    {
        using var workbook = NewWorkbook("Slow Moving Products Report");
        var ws = AddSheet(workbook, "Slow Moving Products", WarningOrange);
        int row = WriteReportHeader(ws, "Slow Moving Products Report", 6, report.FromDate, report.ToDate,
            $"Threshold: {report.DaysThreshold} days without sales");

        var totalValue = report.Products.Sum(p => p.StockValue);
        WriteKpiCard(ws, row, 1, "Slow Moving Items", report.Products.Count, FormatCount);
        WriteKpiCard(ws, row, 2, "Stock Value at Risk", totalValue, FormatUsd, DangerRed);
        WriteKpiCard(ws, row, 3, "Threshold (days)", report.DaysThreshold, FormatCount);
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
            ws.Cell(row, 3).Value = p.CurrentStock; ws.Cell(row, 3).Style.NumberFormat.Format = FormatCount;
            // Dates sort as dates and the never-sold items collect after them, which is
            // the order somebody sorting this column is looking for anyway.
            if (p.LastSoldDate.HasValue)
            {
                ws.Cell(row, 4).Value = p.LastSoldDate.Value;
                ws.Cell(row, 4).Style.NumberFormat.Format = FormatDate;
            }
            else
            {
                ws.Cell(row, 4).Value = "Never";
                ws.Cell(row, 4).Style.Font.FontColor = DangerRed;
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            ws.Cell(row, 5).Value = p.DaysSinceLastSale; ws.Cell(row, 5).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (p.DaysSinceLastSale > 90) { ws.Cell(row, 5).Style.Font.FontColor = DangerRed; ws.Cell(row, 5).Style.Font.Bold = true; }
            else if (p.DaysSinceLastSale > 60) ws.Cell(row, 5).Style.Font.FontColor = WarningOrange;
            ws.Cell(row, 6).Value = p.StockValue; ws.Cell(row, 6).Style.NumberFormat.Format = FormatUsd;
            row++;
        }
        int lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, 6, "Every product has sold within the threshold. Nothing is slow-moving.");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 3, dataStart, lastData, FormatCount);
        WriteSubtotal(ws, row, 6, dataStart, lastData, FormatUsd);
        StyleTotalsRow(ws, row, 6);

        WriteFooter(ws, row, 6);
        FinalizeSheet(ws, 6, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportMerchandiserPurchaseOrderReportToExcel(GetMerchandiserPurchaseOrderReportResult report)
    {
        using var workbook = NewWorkbook("Merchandiser Purchase Order Report");

        var overview = AddSheet(workbook, "Overview");
        int row = WriteReportHeader(overview, "Merchandiser Purchase Order Report", 8, report.FromDate, report.ToDate);

        WriteKpiCard(overview, row, 1, "Merchandisers", report.TotalMerchandisers, FormatCount);
        WriteKpiCard(overview, row, 2, "Orders", report.TotalOrders, FormatCount);
        WriteKpiCard(overview, row, 3, "With PO", report.OrdersWithAttachments, FormatCount, SuccessGreen);
        WriteKpiCard(overview, row, 4, "Without PO", report.OrdersWithoutAttachments, FormatCount, WarningOrange);
        WriteKpiCard(overview, row, 5, "Attachments", report.TotalAttachments, FormatCount);
        WriteKpiCard(overview, row, 6, "Order Value", report.TotalOrderValue, FormatMoney);
        row += 3;

        WriteSectionTitle(overview, row, 8, "MERCHANDISER BREAKDOWN");
        row++;

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
            overview.Range(row, 3, row, 6).Style.NumberFormat.Format = FormatCount;
            overview.Range(row, 3, row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            overview.Cell(row, 7).Value = merchandiser.TotalOrderValue;
            overview.Cell(row, 7).Style.NumberFormat.Format = FormatMoney;
            if (merchandiser.LatestOrderCreatedAtUtc.HasValue)
            {
                overview.Cell(row, 8).Value = IAuditService.ToCAT(EnsureUtc(merchandiser.LatestOrderCreatedAtUtc.Value));
                overview.Cell(row, 8).Style.NumberFormat.Format = FormatTimestamp;
            }
            else
            {
                overview.Cell(row, 8).Value = "Not available";
                overview.Cell(row, 8).Style.Font.FontColor = MutedText;
            }
            row++;
        }

        int lastMerchandiser = row - 1;
        row = FinishTable(overview, freezeAt, dataStart, row, 8, "No merchandiser activity matched the selected filters.");

        overview.Cell(row, 1).Value = "TOTAL";
        for (int col = 3; col <= 6; col++)
        {
            WriteSubtotal(overview, row, col, dataStart, lastMerchandiser, FormatCount);
            overview.Cell(row, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
        WriteSubtotal(overview, row, 7, dataStart, lastMerchandiser, FormatMoney);
        StyleTotalsRow(overview, row, 8);

        WriteFooter(overview, row, 8);
        FinalizeSheet(overview, 8, freezeAt, landscape: true);

        var ordersSheet = AddSheet(workbook, "Orders");
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

        int ordersStart = orderRow;
        foreach (var order in report.Orders)
        {
            ordersSheet.Cell(orderRow, 1).Value = order.OrderNumber;
            ordersSheet.Cell(orderRow, 2).Value = order.AttachmentReference;
            ordersSheet.Cell(orderRow, 3).Value = IAuditService.ToCAT(EnsureUtc(order.CreatedAtUtc));
            ordersSheet.Cell(orderRow, 3).Style.NumberFormat.Format = FormatTimestamp;
            ordersSheet.Cell(orderRow, 4).Value = IAuditService.ToCAT(EnsureUtc(order.OrderDateUtc)).Date;
            ordersSheet.Cell(orderRow, 4).Style.NumberFormat.Format = FormatDate;
            ordersSheet.Cell(orderRow, 5).Value = $"{order.MerchandiserFullName} ({order.MerchandiserUsername})";
            ordersSheet.Cell(orderRow, 6).Value = order.CardCode;
            ordersSheet.Cell(orderRow, 7).Value = order.CardName ?? string.Empty;
            ordersSheet.Cell(orderRow, 8).Value = order.SapDocNum?.ToString() ?? "Pending";
            ordersSheet.Cell(orderRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ordersSheet.Cell(orderRow, 9).Value = order.SapDocEntry?.ToString() ?? "Not synced";
            ordersSheet.Cell(orderRow, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ordersSheet.Cell(orderRow, 10).Value = order.StatusLabel;
            ordersSheet.Cell(orderRow, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ordersSheet.Cell(orderRow, 11).Value = order.IsSynced ? "Yes" : "No";
            ordersSheet.Cell(orderRow, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (!order.IsSynced) ordersSheet.Cell(orderRow, 11).Style.Font.FontColor = WarningOrange;
            ordersSheet.Cell(orderRow, 12).Value = order.AttachmentCount;
            ordersSheet.Cell(orderRow, 12).Style.NumberFormat.Format = FormatCount;
            ordersSheet.Cell(orderRow, 12).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (order.AttachmentCount == 0) ordersSheet.Cell(orderRow, 12).Style.Font.FontColor = WarningOrange;
            ordersSheet.Cell(orderRow, 13).Value = order.Currency ?? string.Empty;
            ordersSheet.Cell(orderRow, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ordersSheet.Cell(orderRow, 14).Value = order.DocTotal;
            ordersSheet.Cell(orderRow, 14).Style.NumberFormat.Format = FormatMoney;
            ordersSheet.Cell(orderRow, 15).Value = order.ItemCount;
            ordersSheet.Cell(orderRow, 15).Style.NumberFormat.Format = FormatCount;
            ordersSheet.Cell(orderRow, 15).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ordersSheet.Cell(orderRow, 16).Value = order.TotalQuantity;
            ordersSheet.Cell(orderRow, 16).Style.NumberFormat.Format = FormatQuantity;
            orderRow++;
        }

        int lastOrder = orderRow - 1;
        orderRow = FinishTable(ordersSheet, ordersFreeze, ordersStart, orderRow, 16, "No orders matched the selected filters.");

        ordersSheet.Cell(orderRow, 1).Value = "TOTAL";
        WriteSubtotal(ordersSheet, orderRow, 12, ordersStart, lastOrder, FormatCount);
        ordersSheet.Cell(orderRow, 12).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(ordersSheet, orderRow, 15, ordersStart, lastOrder, FormatCount);
        ordersSheet.Cell(orderRow, 15).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(ordersSheet, orderRow, 16, ordersStart, lastOrder, FormatQuantity);
        StyleTotalsRow(ordersSheet, orderRow, 16);

        WriteFooter(ordersSheet, orderRow, 16);
        FinalizeSheet(ordersSheet, 16, ordersFreeze, landscape: true);

        var attachmentsSheet = AddSheet(workbook, "Attachments");
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

        int attachmentsStart = attachmentRow;
        foreach (var detail in attachmentDetails)
        {
            attachmentsSheet.Cell(attachmentRow, 1).Value = detail.OrderNumber;
            attachmentsSheet.Cell(attachmentRow, 2).Value = detail.SapDocNum?.ToString() ?? "Pending";
            attachmentsSheet.Cell(attachmentRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            attachmentsSheet.Cell(attachmentRow, 3).Value = detail.AttachmentReference;
            attachmentsSheet.Cell(attachmentRow, 4).Value = $"{detail.MerchandiserFullName} ({detail.MerchandiserUsername})";
            attachmentsSheet.Cell(attachmentRow, 5).Value = $"{detail.CardCode} - {detail.CardName}";
            attachmentsSheet.Cell(attachmentRow, 6).Value = detail.Attachment.FileName;
            attachmentsSheet.Cell(attachmentRow, 7).Value = detail.Attachment.MimeType ?? string.Empty;
            attachmentsSheet.Cell(attachmentRow, 8).Value = detail.Attachment.FileSizeBytes;
            attachmentsSheet.Cell(attachmentRow, 8).Style.NumberFormat.Format = FormatCount;
            attachmentsSheet.Cell(attachmentRow, 9).Value = IAuditService.ToCAT(EnsureUtc(detail.Attachment.UploadedAtUtc));
            attachmentsSheet.Cell(attachmentRow, 9).Style.NumberFormat.Format = FormatTimestamp;
            attachmentsSheet.Cell(attachmentRow, 10).Value = detail.Attachment.UploadedByUsername ?? string.Empty;
            attachmentsSheet.Cell(attachmentRow, 11).Value = detail.Attachment.Description ?? string.Empty;
            attachmentRow++;
        }

        int lastAttachment = attachmentRow - 1;
        attachmentRow = FinishTable(attachmentsSheet, attachmentsFreeze, attachmentsStart, attachmentRow, 11,
            "No uploaded purchase-order attachments were returned for this report.");

        attachmentsSheet.Cell(attachmentRow, 1).Value = "TOTAL";
        WriteSubtotal(attachmentsSheet, attachmentRow, 8, attachmentsStart, lastAttachment, FormatCount);
        StyleTotalsRow(attachmentsSheet, attachmentRow, 11);

        WriteFooter(attachmentsSheet, attachmentRow, 11);
        FinalizeSheet(attachmentsSheet, 11, attachmentsFreeze, landscape: true);

        var linesSheet = AddSheet(workbook, "Order Lines");
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

        int linesStart = lineRow;
        foreach (var detail in lineDetails)
        {
            linesSheet.Cell(lineRow, 1).Value = detail.OrderNumber;
            linesSheet.Cell(lineRow, 2).Value = detail.SapDocNum?.ToString() ?? "Pending";
            linesSheet.Cell(lineRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            linesSheet.Cell(lineRow, 3).Value = detail.AttachmentReference;
            linesSheet.Cell(lineRow, 4).Value = $"{detail.MerchandiserFullName} ({detail.MerchandiserUsername})";
            linesSheet.Cell(lineRow, 5).Value = $"{detail.CardCode} - {detail.CardName}";
            linesSheet.Cell(lineRow, 6).Value = detail.Line.LineNum;
            linesSheet.Cell(lineRow, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            linesSheet.Cell(lineRow, 7).Value = detail.Line.ItemCode;
            linesSheet.Cell(lineRow, 8).Value = detail.Line.ItemDescription ?? string.Empty;
            linesSheet.Cell(lineRow, 9).Value = detail.Line.Quantity;
            linesSheet.Cell(lineRow, 9).Style.NumberFormat.Format = FormatQuantity;
            linesSheet.Cell(lineRow, 10).Value = detail.Line.QuantityFulfilled;
            linesSheet.Cell(lineRow, 10).Style.NumberFormat.Format = FormatQuantity;
            linesSheet.Cell(lineRow, 11).Value = detail.Line.WarehouseCode ?? string.Empty;
            linesSheet.Cell(lineRow, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            linesSheet.Cell(lineRow, 12).Value = detail.Line.LineTotal;
            linesSheet.Cell(lineRow, 12).Style.NumberFormat.Format = FormatMoney;
            lineRow++;
        }

        int lastLine = lineRow - 1;
        lineRow = FinishTable(linesSheet, linesFreeze, linesStart, lineRow, 12, "No line items were returned for this report.");

        linesSheet.Cell(lineRow, 1).Value = "TOTAL";
        WriteSubtotal(linesSheet, lineRow, 9, linesStart, lastLine, FormatQuantity);
        WriteSubtotal(linesSheet, lineRow, 10, linesStart, lastLine, FormatQuantity);
        StyleTotalsRow(linesSheet, lineRow, 12);

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
    private static readonly XLColor PodProductTypeBg = XLColor.FromHtml("#E3F2FD");
    private static readonly XLColor PodCrateTypeBg = XLColor.FromHtml("#F3E5F5");
    private static readonly XLColor PodCombinedTypeBg = XLColor.FromHtml("#E8F5E9");
    private static readonly XLColor PodGreen = XLColor.FromHtml("#2E7D32");
    private static readonly XLColor PodOrange = XLColor.FromHtml("#E65100");
    private static readonly XLColor PodRed = XLColor.FromHtml("#C62828");
    private static readonly XLColor PodProductBlue = XLColor.FromHtml("#1565C0");
    private static readonly XLColor PodCratePurple = XLColor.FromHtml("#6A1B9A");
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
        ws.Style.Font.FontName = ReportFont;
        ws.Style.Font.FontSize = 10;
        // The sheet's own grid showing through a styled table is the detail that makes
        // an export look like a data dump rather than a report.
        ws.ShowGridLines = false;
        ws.TabColor = PodNavy;
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

    private static void PodFinalize(IXLWorksheet ws, int lastCol, int freezeRow = 0, int freezeCol = 0, int filterLastRow = 0)
    {
        ws.Columns(1, lastCol).AdjustToContents();
        for (int columnIndex = 1; columnIndex <= lastCol; columnIndex++)
        {
            if (ws.Column(columnIndex).Width > 42) ws.Column(columnIndex).Width = 42;
            if (ws.Column(columnIndex).Width < 11) ws.Column(columnIndex).Width = 11;
        }

        if (freezeRow > 0) ws.SheetView.FreezeRows(freezeRow);
        if (freezeCol > 0) ws.SheetView.FreezeColumns(freezeCol);

        if (freezeRow > 0 && filterLastRow > freezeRow && !ws.AutoFilter.IsEnabled)
        {
            ws.Range(freezeRow, 1, filterLastRow, lastCol).SetAutoFilter();
        }

        if (freezeRow > 0)
        {
            ws.PageSetup.SetRowsToRepeatAtTop(freezeRow, freezeRow);
        }

        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetLeft(0.4);
        ws.PageSetup.Margins.SetRight(0.4);
        ws.PageSetup.Margins.SetTop(0.4);
        ws.PageSetup.Margins.SetBottom(0.4);
        ApplyPrintHeaderFooter(ws);
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

    /// <summary>
    /// The delivery routes the customer is called on. A shop can sit on two, and
    /// a shop the routes workbook never placed on a truck sits on none, so this
    /// is a list rather than a single route.
    /// </summary>
    private static string FormatPodRouteDisplay(PodUploadStatusItem item)
    {
        var routes = DeliveryRoutes.FormatRoutes(item.CardCode);
        return routes.Length > 0 ? routes : "-";
    }

    private static bool HasProductPod(PodUploadStatusItem item) =>
        item.HasProductPod || item.ProductPodCount > 0;

    private static bool HasCratePod(PodUploadStatusItem item) =>
        item.HasCratePod || item.CratePodCount > 0;

    private static string FormatPodTypeDisplay(PodUploadStatusItem item)
    {
        var hasProductPod = HasProductPod(item);
        var hasCratePod = HasCratePod(item);

        return (hasProductPod, hasCratePod) switch
        {
            (true, true) => "Product POD",
            (true, false) => "Product POD",
            (false, true) => "Crate POD",
            _ when item.HasPod => "POD Uploaded",
            _ => "No POD uploaded"
        };
    }

    private static IEnumerable<string> GetPodTypeLabels(PodUploadStatusItem item)
    {
        if (HasProductPod(item))
        {
            yield return "Product POD";
            yield break;
        }

        if (HasCratePod(item))
            yield return "Crate POD";

        if (!HasProductPod(item) && !HasCratePod(item) && item.HasPod)
            yield return "POD Uploaded";
    }

    private static bool IsPodReportExcludedInvoice(PodUploadStatusItem item) =>
        IsPodExcelExcludedBusinessPartner(item)
        || IsPodExcelExcludedCreatorUser(item)
        || string.IsNullOrWhiteSpace(item.CreatedLocation);

    private static bool IsPodExcelExcludedBusinessPartner(PodUploadStatusItem item) =>
        !string.IsNullOrWhiteSpace(item.CardCode)
        && PodExcelExcludedBusinessPartnerCodes.Contains(item.CardCode.Trim());

    private static bool IsPodExcelExcludedCreatorUser(PodUploadStatusItem item) =>
        item.CreatedByUserId.HasValue
        && PodExcelExcludedCreatorUserIds.Contains(item.CreatedByUserId.Value);

    internal static PodUploadStatusReport ApplyPodReportingScope(PodUploadStatusReport report)
    {
        var items = report.Items
            .Where(item => !IsPodReportExcludedInvoice(item))
            .ToList();

        return new PodUploadStatusReport
        {
            FromDate = report.FromDate,
            ToDate = report.ToDate,
            TotalInvoices = items.Count,
            UploadedCount = items.Count(item => item.HasPod),
            PendingCount = items.Count(item => !item.HasPod),
            CreditNoteDataComplete = report.CreditNoteDataComplete,
            CreditNoteDataWarning = report.CreditNoteDataWarning,
            Items = items
        };
    }

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

    private static void StylePodTypeCell(IXLCell cell, PodUploadStatusItem item)
    {
        var hasProductPod = HasProductPod(item);
        var hasCratePod = HasCratePod(item);

        cell.Style.Font.Bold = true;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        cell.Style.Alignment.WrapText = true;

        if (hasProductPod && hasCratePod)
        {
            cell.Style.Fill.BackgroundColor = PodCombinedTypeBg;
            cell.Style.Font.FontColor = PodGreen;
            return;
        }

        if (hasCratePod)
        {
            cell.Style.Fill.BackgroundColor = PodCrateTypeBg;
            cell.Style.Font.FontColor = PodCratePurple;
            return;
        }

        if (hasProductPod)
        {
            cell.Style.Fill.BackgroundColor = PodProductTypeBg;
            cell.Style.Font.FontColor = PodProductBlue;
            return;
        }

        cell.Style.Fill.BackgroundColor = PodPendingBg;
        cell.Style.Font.FontColor = item.HasPod ? PodGreen : PodOrange;
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
        using var workbook = NewWorkbook("POD Upload Status Report");
        var now = CurrentCatNow();
        var periodText = FormatPodReportPeriod(report);
        var reportItems = ApplyPodReportingScope(report).Items;

        var productInvoices = reportItems.Where(item => !item.IsCrateInvoice).ToList();
        var crateInvoices = reportItems.Where(item => item.IsCrateInvoice).ToList();

        BuildPodInvoiceSheet(
            workbook,
            "Product Invoices",
            "PRODUCT",
            productInvoices,
            periodText,
            now,
            report.CreditNoteDataComplete);
        BuildPodInvoiceSheet(
            workbook,
            "Crate Invoices",
            "CRATE",
            crateInvoices,
            periodText,
            now,
            report.CreditNoteDataComplete);

        var pendingAmount = reportItems.Where(item => !item.HasPod).Sum(item => item.DocTotal);

        var pending = reportItems.Where(item => !item.HasPod).OrderBy(item => item.DocDate).ToList();
        {
            var ws = workbook.Worksheets.Add("Pending PODs");
            const int lastCol = 11;
            PodApplyDefaults(ws);

            var oldestPendingDays = pending.Count > 0
                ? pending.Max(item => CalculatePodDaysAging(item.DocDate, now))
                : 0;
            var stalePendingCount = pending.Count(item => CalculatePodDaysAging(item.DocDate, now) > 14);

            var row = PodTitleBar(ws, $"PENDING PRODUCT / CRATE POD UPLOADS - {periodText}", lastCol, now);
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
                "Delivery Route",
                "Invoice Date",
                "Generated Location",
                "POD Type",
                "Days Aging",
                "Credit Note #",
                "Credit Reason",
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
                ws.Cell(row, 4).Value = FormatPodRouteDisplay(item);
                ws.Cell(row, 4).Style.Font.FontColor = PodTextMuted;
                WriteDateCell(ws.Cell(row, 5), item.DocDate);
                ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 6).Value = FormatPodGeneratedLocationDisplay(item);
                ws.Cell(row, 6).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 7).Value = FormatPodTypeDisplay(item);
                StylePodTypeCell(ws.Cell(row, 7), item);
                ws.Cell(row, 8).Value = daysAging;
                StylePodAgingCell(ws.Cell(row, 8), daysAging);
                WritePodCreditNoteCells(
                    ws,
                    row,
                    9,
                    10,
                    item,
                    report.CreditNoteDataComplete);
                ws.Cell(row, 11).Value = item.DocTotal;
                StylePodTotalCell(ws.Cell(row, 11), isStripe);

                row++;
                rowIndex++;
            }

            var podLastDataRow = row - 1;
            PodSummaryRow(ws, row, lastCol);
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 2).Value = $"{pending.Count:N0} invoices";
            ws.Cell(row, 7).Value = "No POD uploaded";
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 8).Value = $"Oldest: {oldestPendingDays:N0} days";
            ws.Cell(row, 9).Value = report.CreditNoteDataComplete
                ? $"{pending.Count(item => item.IsFullyCredited):N0} fully credited"
                : $"{pending.Count(item => item.IsFullyCredited):N0} confirmed fully credited";
            ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 10).Value = "Reasons shown where supplied";
            ws.Cell(row, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 11).Value = pendingAmount;
            ws.Cell(row, 11).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            PodDisclaimerRow(ws, row + 2, lastCol, now);
            PodFinalize(ws, lastCol, headerRow, 2, podLastDataRow);
            ws.Column(1).Width = 12;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 24;
            ws.Column(5).Width = 14;
            ws.Column(6).Width = 22;
            ws.Column(7).Width = 20;
            ws.Column(8).Width = 12;
            ws.Column(9).Width = 16;
            ws.Column(10).Width = 32;
            ws.Column(11).Width = 14;
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
                PodTypes = group
                    .SelectMany(entry => GetPodTypeLabels(entry.Item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label)
                    .ToList(),
                LatestUpload = group.Max(entry => entry.Uploader.LatestUploadedAt)
            })
            .OrderByDescending(group => group.UploadedInvoices)
            .ThenBy(group => group.UploadedBy)
            .ToList();

        if (uploadsByUser.Any())
        {
            var ws = workbook.Worksheets.Add("Uploads By User");
            const int lastCol = 6;
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
                "POD Types",
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
                ws.Cell(row, 5).Value = group.PodTypes.Count > 0
                    ? string.Join(", ", group.PodTypes)
                    : "POD Uploaded";
                ws.Cell(row, 5).Style.Font.Bold = true;
                ws.Cell(row, 5).Style.Alignment.WrapText = true;
                ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 6).Value = FormatPodUploadDate(group.LatestUpload);
                if (!group.LatestUpload.HasValue)
                    StylePodMutedCell(ws.Cell(row, 6));

                row++;
                rowIndex++;
            }

            var podLastDataRow = row - 1;
            PodSummaryRow(ws, row, lastCol);
            ws.Cell(row, 1).Value = "SUMMARY";
            ws.Cell(row, 2).Value = uploadsByUser.Sum(group => group.UploadedInvoices);
            ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 3).Value = uploadsByUser.Sum(group => group.TotalFiles);
            ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 4).Value = uploadsByUser.Sum(group => group.TotalAmount);
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, 5).Value = string.Join(", ", uploadsByUser
                .SelectMany(group => group.PodTypes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label));
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 6).Value = uploadsByUser.Count == 1
                ? uploadsByUser[0].UploadedBy
                : $"{uploadsByUser.Count:N0} uploaders";

            PodDisclaimerRow(ws, row + 2, lastCol, now);
            PodFinalize(ws, lastCol, headerRow, 1, podLastDataRow);
            ws.Column(1).Width = 28;
            ws.Column(2).Width = 16;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 16;
            ws.Column(5).Width = 24;
            ws.Column(6).Width = 22;
        }

        var uploaded = reportItems.Where(item => item.HasPod).OrderByDescending(item => item.PodUploadedAt).ToList();
        var uploadedAmount = uploaded.Sum(item => item.DocTotal);
        {
            var ws = workbook.Worksheets.Add("Uploaded PODs");
            const int lastCol = 12;
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

            var row = PodTitleBar(ws, $"INVOICES WITH PRODUCT / CRATE POD UPLOADED - {periodText}", lastCol, now);
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
                "Delivery Route",
                "Invoice Date",
                "Generated Location",
                "POD Type",
                "Uploaded",
                "Uploaded By",
                "Credit Note #",
                "Credit Reason",
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
                ws.Cell(row, 4).Value = FormatPodRouteDisplay(item);
                ws.Cell(row, 4).Style.Font.FontColor = PodTextMuted;
                WriteDateCell(ws.Cell(row, 5), item.DocDate);
                ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 6).Value = FormatPodGeneratedLocationDisplay(item);
                ws.Cell(row, 6).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 7).Value = FormatPodTypeDisplay(item);
                StylePodTypeCell(ws.Cell(row, 7), item);
                ws.Cell(row, 8).Value = FormatPodUploadDate(item.PodUploadedAt);
                ws.Cell(row, 8).Style.Font.FontColor = PodTextMuted;
                ws.Cell(row, 9).Value = FormatPodUploadedByDisplay(item);
                ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                WritePodCreditNoteCells(
                    ws,
                    row,
                    10,
                    11,
                    item,
                    report.CreditNoteDataComplete);
                ws.Cell(row, 12).Value = item.DocTotal;
                StylePodTotalCell(ws.Cell(row, 12), isStripe);

                row++;
                rowIndex++;
            }

            var podLastDataRow = row - 1;
            PodSummaryRow(ws, row, lastCol);
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 2).Value = $"{uploaded.Count:N0} invoices";
            ws.Cell(row, 7).Value = $"{uploadedFileCount:N0} files";
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 9).Value = $"{uploadedUsers:N0} uploaders";
            ws.Cell(row, 10).Value = report.CreditNoteDataComplete
                ? $"{uploaded.Count(item => item.IsFullyCredited):N0} fully credited"
                : $"{uploaded.Count(item => item.IsFullyCredited):N0} confirmed fully credited";
            ws.Cell(row, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 11).Value = "Reasons shown where supplied";
            ws.Cell(row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 12).Value = uploadedAmount;
            ws.Cell(row, 12).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 12).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            PodDisclaimerRow(ws, row + 2, lastCol, now);
            PodFinalize(ws, lastCol, headerRow, 2, podLastDataRow);
            ws.Column(1).Width = 12;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 24;
            ws.Column(5).Width = 14;
            ws.Column(6).Width = 22;
            ws.Column(7).Width = 20;
            ws.Column(8).Width = 22;
            ws.Column(9).Width = 16;
            ws.Column(10).Width = 16;
            ws.Column(11).Width = 32;
            ws.Column(12).Width = 14;
        }

        return WorkbookToBytes(workbook);
    }

    private static void BuildPodInvoiceSheet(
        XLWorkbook workbook,
        string sheetName,
        string invoiceType,
        IReadOnlyCollection<PodUploadStatusItem> reportItems,
        string periodText,
        DateTime now,
        bool creditNoteDataComplete)
    {
        var totalInvoices = reportItems.Count;
        var uploadedCount = reportItems.Count(item => item.HasPod);
        var pendingCount = totalInvoices - uploadedCount;
        var totalAmount = reportItems.Sum(item => item.DocTotal);
        var pendingAmount = reportItems.Where(item => !item.HasPod).Sum(item => item.DocTotal);
        var completionPct = totalInvoices > 0
            ? uploadedCount / (double)totalInvoices * 100
            : 0;

        var ws = workbook.Worksheets.Add(sheetName);
        const int lastCol = 13;
        PodApplyDefaults(ws);

        var row = PodTitleBar(ws, $"{invoiceType} POD UPLOAD STATUS - {periodText}", lastCol, now);
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
            "Delivery Route",
            "Invoice Date",
            "Generated Location",
            "Amount",
            "POD Status",
            "POD Type",
            "Uploaded",
            "Credit Note #",
            "Credit Reason",
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
            ws.Cell(row, 4).Value = FormatPodRouteDisplay(item);
            ws.Cell(row, 4).Style.Font.FontColor = PodTextMuted;
            WriteDateCell(ws.Cell(row, 5), item.DocDate);
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 6).Value = FormatPodGeneratedLocationDisplay(item);
            ws.Cell(row, 6).Style.Font.FontColor = PodTextMuted;
            ws.Cell(row, 7).Value = item.DocTotal;
            StylePodCurrencyCell(ws.Cell(row, 7));

            ws.Cell(row, 8).Value = item.HasPod ? "Uploaded" : "Pending";
            StylePodStatusCell(ws.Cell(row, 8), item.HasPod, isStripe);

            ws.Cell(row, 9).Value = FormatPodTypeDisplay(item);
            StylePodTypeCell(ws.Cell(row, 9), item);

            if (item.HasPod && item.PodUploadedAt.HasValue)
            {
                var uploadStr = FormatPodUploadDate(item.PodUploadedAt);
                var uploaderDisplay = FormatPodUploadedByDisplay(item);
                if (!string.IsNullOrEmpty(uploaderDisplay) && uploaderDisplay != "-")
                    uploadStr += $" ({uploaderDisplay})";
                ws.Cell(row, 10).Value = uploadStr;
                ws.Cell(row, 10).Style.Font.FontColor = PodTextMuted;
            }
            else
            {
                ws.Cell(row, 10).Value = "-";
                StylePodMutedCell(ws.Cell(row, 10));
            }

            WritePodCreditNoteCells(
                ws,
                row,
                11,
                12,
                item,
                creditNoteDataComplete);

            ws.Cell(row, 13).Value = item.DocTotal;
            StylePodTotalCell(ws.Cell(row, 13), isStripe);

            row++;
            rowIndex++;
        }

        var podLastDataRow = row - 1;
        PodSummaryRow(ws, row, lastCol);
        ws.Cell(row, 1).Value = "SUMMARY";
        ws.Cell(row, 2).Value = $"{totalInvoices:N0} invoices";
        ws.Cell(row, 5).Value = periodText;
        ws.Cell(row, 7).Value = totalAmount;
        ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(row, 8).Value = $"{uploadedCount:N0} uploaded / {pendingCount:N0} pending";
        ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 9).Value = $"{invoiceType.ToLowerInvariant()} invoices only";
        ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 11).Value = creditNoteDataComplete
            ? $"{reportItems.Count(item => item.IsFullyCredited):N0} fully credited"
            : $"{reportItems.Count(item => item.IsFullyCredited):N0} confirmed fully credited";
        ws.Cell(row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 12).Value = "Reasons shown where supplied";
        ws.Cell(row, 12).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(row, 13).Value = totalAmount;
        ws.Cell(row, 13).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        PodDisclaimerRow(ws, row + 2, lastCol, now);
        PodFinalize(ws, lastCol, headerRow, 2, podLastDataRow);
        ws.Column(1).Width = 12;
        ws.Column(2).Width = 38;
        ws.Column(3).Width = 12;
        ws.Column(4).Width = 24;
        ws.Column(5).Width = 14;
        ws.Column(6).Width = 22;
        ws.Column(7).Width = 14;
        ws.Column(8).Width = 12;
        ws.Column(9).Width = 20;
        ws.Column(10).Width = 28;
        ws.Column(11).Width = 16;
        ws.Column(12).Width = 32;
        ws.Column(13).Width = 14;
    }

    private static void WritePodCreditNoteCells(
        IXLWorksheet ws,
        int row,
        int creditNoteColumn,
        int reasonColumn,
        PodUploadStatusItem item,
        bool creditNoteDataComplete)
    {
        if (string.IsNullOrWhiteSpace(item.CreditNoteNumber))
        {
            ws.Cell(row, creditNoteColumn).Value = creditNoteDataComplete ? "-" : "Not verified";
            ws.Cell(row, reasonColumn).Value = creditNoteDataComplete ? "-" : "Pending verification";
            StylePodMutedCell(ws.Cell(row, creditNoteColumn));
            StylePodMutedCell(ws.Cell(row, reasonColumn));
            return;
        }

        ws.Cell(row, creditNoteColumn).Value = item.CreditNoteNumber.Trim();
        ws.Cell(row, creditNoteColumn).Style.Font.Bold = true;
        ws.Cell(row, creditNoteColumn).Style.Font.FontColor = item.IsFullyCredited
            ? PodRed
            : PodTextDark;
        ws.Cell(row, creditNoteColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(row, reasonColumn).Value = string.IsNullOrWhiteSpace(item.CreditNoteReason)
            ? "-"
            : item.CreditNoteReason.Trim();
        ws.Cell(row, reasonColumn).Style.Font.FontColor = string.IsNullOrWhiteSpace(item.CreditNoteReason)
            ? PodTextMuted
            : PodTextDark;
        ws.Cell(row, reasonColumn).Style.Alignment.WrapText = true;
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
        using var workbook = NewWorkbook("Timesheet Report");
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildTimesheetOverviewSheet(workbook, report, fromDate, toDate, now);

        foreach (var user in report.UserSummaries.OrderByDescending(u => u.TotalVisits))
            BuildTimesheetUserSheet(workbook, user, fromDate, toDate, now);

        return WorkbookToBytes(workbook);
    }

    private static void TsApplyDefaults(IXLWorksheet ws)
    {
        ws.Style.Font.FontName = ReportFont;
        ws.Style.Font.FontSize = 10;
        ws.ShowGridLines = false;
        ws.TabColor = TsNavy;
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
        // No autofilter here: these sheets stack several tables under one title bar and
        // Excel allows one filter per sheet, so it would attach to whichever table came
        // first and silently mislead about the rest.
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetLeft(0.4);
        ws.PageSetup.Margins.SetRight(0.4);
        ws.PageSetup.Margins.SetTop(0.4);
        ws.PageSetup.Margins.SetBottom(0.4);
        ApplyPrintHeaderFooter(ws);
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
                // A real date, so the column sorts chronologically and filters to a week.
                ws.Cell(row, 1).Value = day.Date;
                ws.Cell(row, 1).Style.NumberFormat.Format = FormatDate;
                ws.Cell(row, 2).Value = day.Date;
                ws.Cell(row, 2).Style.NumberFormat.Format = "ddd";
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
            // A real date, so the column sorts chronologically and filters to a week.
            ws.Cell(row, 1).Value = day.Date;
            ws.Cell(row, 1).Style.NumberFormat.Format = FormatDate;
            ws.Cell(row, 2).Value = day.Date;
            ws.Cell(row, 2).Style.NumberFormat.Format = "ddd";
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

    // ── Van attendance ──────────────────────────────────────────────────────
    //
    // Its own workbook, from the van's own report. It borrows the Ts* helpers above, which are
    // presentation only — a title bar, a KPI strip, a striped data row. Nothing about van and
    // merchandiser data mixes here: the two exports take different models built by different
    // handlers, and neither can be handed the other's.
    //
    // What differs from the timesheet workbook is what a van is measured on. Open calls get a
    // column of their own rather than being inferred from a completion percentage, because on a
    // van they are the finding — a rep who checked in and drove off without checking out leaves a
    // call with no duration, and the sheet has to name that rather than average it away.

    // ═══════════════════════════════════════════════════════════════
    //  Van sales performance
    // ═══════════════════════════════════════════════════════════════
    //
    // On the Ts* ramp rather than the classic one, matching the van attendance workbook beside it:
    // both are read per rep over a period, and a reader moving between them should not have to
    // re-learn where the figures sit.
    //
    // Two rules the sheets below never break, both inherited from the report itself:
    //
    //  - Money is written as text, per currency, not as a number. A numeric column would invite a
    //    reader to select it and watch Excel add USD to ZiG, and the sum would be a number that
    //    describes nothing. Where a single currency is certain — inside the drop-size sheet, which
    //    is per currency by construction — the figures are real numbers and can be totalled.
    //  - Quantity is written against its unit for the same reason, and van lines carry no unit, so
    //    most rows will read "unit not recorded". That is the honest answer.

    public byte[] ExportVanSalesPerformanceToExcel(VanSalesPerformanceReportResponse report)
    {
        using var workbook = NewWorkbook("Van Sales Performance");
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildVanPerformanceOverviewSheet(workbook, report, now);
        BuildVanPerformanceRepSheet(workbook, report, now);
        BuildVanPerformanceItemSheet(workbook, report, now);
        BuildVanPerformancePriceSheet(workbook, report, now);
        BuildVanPerformanceTrendSheet(workbook, report, now);
        BuildVanPerformanceDropSheet(workbook, report, now);

        return WorkbookToBytes(workbook);
    }

    private static void BuildVanPerformanceOverviewSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        DateTime now)
    {
        const int lastCol = 10;
        var ws = workbook.Worksheets.Add("Overview");
        TsApplyDefaults(ws);

        var period = $"VAN SALES PERFORMANCE  —  {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}";
        int row = TsTitleBar(ws, period, lastCol, now);

        var summary = report.Summary;
        var lead = summary.TotalsByCurrency.FirstOrDefault();

        row = TsKpiStrip(ws, row, lastCol,
            ("Gross Takings", lead is null ? "—" : $"{lead.Currency} {lead.Gross:N0}", null),
            ("Documents", summary.DocumentCount.ToString("N0"), null),
            ("Productive Calls", summary.ProductiveCalls.ToString("N0"), null),
            ("Strike Rate", summary.StrikeRate is { } rate ? $"{rate:P0}" : "—", null),
            ("Avg Drop", lead?.AverageDropSize is { } drop ? $"{lead.Currency} {drop:N0}" : "—", null),
            ("Outlets Bought", summary.CustomerCount.ToString("N0"), null),
            ("New Outlets", summary.NewOutlets.ToString("N0"), null),
            ("Items Sold", summary.ItemCount.ToString("N0"), null),
            ("Reps", summary.RepCount.ToString("N0"), null),
            ("Kilometres", summary.KilometresTravelled?.ToString("N0") ?? "—", null));

        // The caveats first, not last. A reader who scrolls past the tables has already formed a
        // view; what the figures could not see has to reach them before the figures do.
        if (!report.Coverage.IsClean)
        {
            TsSectionTitle(ws, row, lastCol, "WHAT THIS PERIOD COULD NOT ANSWER");
            row += 2;

            foreach (var caveat in report.Coverage.Caveats)
            {
                ws.Cell(row, 1).Value = caveat;
                ws.Range(row, 1, row, lastCol).Merge();
                ws.Cell(row, 1).Style.Font.FontSize = 9;
                ws.Cell(row, 1).Style.Font.Italic = true;
                ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
                row++;
            }

            row++;
        }

        if (report.Territories.Count > 0)
        {
            TsSectionTitle(ws, row, lastCol, "TERRITORIES");
            row += 2;
            row = TsColumnHeaders(ws, row, lastCol,
                ["Territory", "Routes", "Rep-Days", "Productive Calls", "Outlets", "Takings", "", "", "", ""]);

            int index = 0;
            foreach (var territory in report.Territories)
            {
                TsDataRow(ws, row, lastCol, index % 2 == 1);
                ws.Cell(row, 1).Value = territory.DisplayTerritory;
                ws.Cell(row, 2).Value = territory.RouteCount;
                ws.Cell(row, 3).Value = territory.TradingDayCount;
                ws.Cell(row, 4).Value = territory.ProductiveCalls;
                ws.Cell(row, 5).Value = territory.CustomerCount;
                ws.Cell(row, 6).Value = MoneyText(territory.TotalsByCurrency);
                row++;
                index++;
            }

            row++;
        }

        TsSectionTitle(ws, row, lastCol, "ROUTES");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Route", "Territory", "Reps", "Rep-Days", "Planned", "Calls", "CCR", "PCR", "Km", "Takings"]);

        int routeIndex = 0;
        foreach (var route in report.Routes)
        {
            TsDataRow(ws, row, lastCol, routeIndex % 2 == 1);
            ws.Cell(row, 1).Value = route.DisplayRoute;
            ws.Cell(row, 2).Value = route.Territory ?? "—";
            ws.Cell(row, 3).Value = route.RepCount;
            ws.Cell(row, 4).Value = route.TradingDayCount;
            ws.Cell(row, 5).Value = route.PlannedCalls?.ToString("N0") ?? "—";
            ws.Cell(row, 6).Value = route.Calls?.ToString("N0") ?? "—";
            ws.Cell(row, 7).Value = RateText(route.CallComplianceRate);
            ws.Cell(row, 8).Value = RateText(route.ProductiveCallRate);
            ws.Cell(row, 9).Value = route.KilometresTravelled?.ToString("N0") ?? "—";
            ws.Cell(row, 10).Value = MoneyText(route.TotalsByCurrency);
            row++;
            routeIndex++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildVanPerformanceRepSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        DateTime now)
    {
        const int lastCol = 12;
        var ws = workbook.Worksheets.Add("Reps");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "REP LEAGUE TABLE", lastCol, now);
        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Rep", "Routes", "Days", "Calls", "Bought", "Strike Rate",
            "Calls/Day", "Outlets", "New", "New Who Bought", "Items", "Takings"
        ]);

        int index = 0;
        foreach (var rep in report.Reps)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = rep.DisplayName;
            ws.Cell(row, 2).Value = rep.Routes.Count == 0 ? "—" : string.Join(", ", rep.Routes);
            ws.Cell(row, 3).Value = rep.TradingDayCount;
            ws.Cell(row, 4).Value = rep.Calls?.ToString("N0") ?? "—";
            ws.Cell(row, 5).Value = rep.ProductiveCalls;
            ws.Cell(row, 6).Value = RateText(rep.StrikeRate);
            ws.Cell(row, 7).Value = rep.CallsPerDay is { } perDay ? perDay.ToString("N1") : "—";
            ws.Cell(row, 8).Value = rep.CustomerCount;
            ws.Cell(row, 9).Value = rep.NewOutlets;
            ws.Cell(row, 10).Value = rep.NewOutletsWhoBought;
            ws.Cell(row, 11).Value = rep.ItemCount;
            ws.Cell(row, 12).Value = MoneyText(rep.TotalsByCurrency);
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildVanPerformanceItemSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        DateTime now)
    {
        const int lastCol = 9;
        var ws = workbook.Worksheets.Add("Items");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "WHAT MOVED", lastCol, now);

        TsSectionTitle(ws, row, lastCol, "RANKED ON REACH — LINES WRITTEN AND OUTLETS REACHED");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["#", "Item Code", "Description", "Lines", "Outlets", "Reps", "Days", "Quantity", "Value"]);

        int index = 0;
        foreach (var item in report.Items)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = item.Rank;
            ws.Cell(row, 2).Value = item.ItemCode;
            ws.Cell(row, 3).Value = item.ItemDescription ?? "—";
            ws.Cell(row, 4).Value = item.LineCount;
            ws.Cell(row, 5).Value = item.CustomerCount;
            ws.Cell(row, 6).Value = item.RepCount;
            ws.Cell(row, 7).Value = item.TradingDayCount;
            ws.Cell(row, 8).Value = QuantityText(item.QuantitiesByUoM);
            ws.Cell(row, 9).Value = LineMoneyText(item.TotalsByCurrency);
            row++;
            index++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "STOPPED SELLING — SOLD IN THE PRECEDING PERIOD, NOT IN THIS ONE");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Item Code", "Description", "Last Sold", "Days Ago", "Lines Before", "Outlets Before", "Value Before", "", ""]);

        int lapsedIndex = 0;
        foreach (var item in report.LapsedItems)
        {
            TsDataRow(ws, row, lastCol, lapsedIndex % 2 == 1);
            ws.Cell(row, 1).Value = item.ItemCode;
            ws.Cell(row, 2).Value = item.ItemDescription ?? "—";
            WriteVanPerformanceDate(ws.Cell(row, 3), item.LastSoldOn);
            ws.Cell(row, 4).Value = item.DaysSinceLastSale;
            ws.Cell(row, 5).Value = item.PriorLineCount;
            ws.Cell(row, 6).Value = item.PriorCustomerCount;
            ws.Cell(row, 7).Value = LineMoneyText(item.PriorTotalsByCurrency);
            row++;
            lapsedIndex++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildVanPerformancePriceSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        DateTime now)
    {
        const int lastCol = 9;
        var ws = workbook.Worksheets.Add("Price Realisation");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "PRICE REALISATION", lastCol, now);

        // Said on the sheet, not only on the page. A workbook is forwarded, and whoever opens it
        // second will not have read the page it came from.
        ws.Cell(row, 1).Value =
            "Peer-relative. Each rep's achieved price against what the same item, unit and currency "
            + "fetched across everybody. Not measured against a list price — there is no trustworthy "
            + "local one. Discounts are not reported because neither van path records one: every van "
            + "line reads 0%.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 28;
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
            ["Item Code", "Description", "Currency", "Unit", "Lines", "Average", "Lowest", "Highest", "Spread %"]);

        int index = 0;
        foreach (var price in report.ItemPrices)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = price.ItemCode;
            ws.Cell(row, 2).Value = price.ItemDescription ?? "—";
            ws.Cell(row, 3).Value = price.Currency;
            ws.Cell(row, 4).Value = price.UoMCode ?? "not recorded";
            ws.Cell(row, 5).Value = price.LineCount;

            // Single item, single currency, single unit: these are safe as real numbers.
            ws.Cell(row, 6).Value = price.WeightedAveragePrice;
            ws.Cell(row, 7).Value = price.MinUnitPrice;
            ws.Cell(row, 8).Value = price.MaxUnitPrice;
            ws.Range(row, 6, row, 8).Style.NumberFormat.Format = "#,##0.00";

            ws.Cell(row, 9).Value = price.PriceSpreadPercent is { } spread ? spread.ToString("N1") : "—";
            row++;
            index++;

            foreach (var rep in price.Reps)
            {
                TsDataRow(ws, row, lastCol, true);
                ws.Cell(row, 2).Value = $"   {rep.DisplayName}";
                ws.Cell(row, 2).Style.Font.Italic = true;
                ws.Cell(row, 5).Value = rep.LineCount;
                ws.Cell(row, 6).Value = rep.WeightedAveragePrice;
                ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 9).Value = rep.VarianceFromItemPercent is { } variance
                    ? variance.ToString("N1")
                    : "—";
                row++;
            }
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildVanPerformanceTrendSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        DateTime now)
    {
        const int lastCol = 7;
        var ws = workbook.Worksheets.Add("Trend");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "TREND", lastCol, now);

        TsSectionTitle(ws, row, lastCol, "BY DAY OF WEEK — AVERAGES DIVIDE BY DAYS IN PERIOD, NOT DAYS TRADED");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Day", "In Period", "Traded", "Documents", "Per Day", "Productive Calls", "Takings"]);

        int index = 0;
        foreach (var point in report.Trend.DayOfWeek)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = point.Label;
            ws.Cell(row, 2).Value = point.CalendarDayCount;
            ws.Cell(row, 3).Value = point.ActiveDayCount;
            ws.Cell(row, 4).Value = point.DocumentCount;
            ws.Cell(row, 5).Value = point.DocumentsPerCalendarDay is { } perDay ? perDay.ToString("N1") : "—";
            ws.Cell(row, 6).Value = point.ProductiveCalls;
            ws.Cell(row, 7).Value = MoneyText(point.TotalsByCurrency);
            row++;
            index++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "BY MONTH");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Month", "Part Month", "Days", "Traded", "Documents", "Productive Calls", "Takings"]);

        int monthIndex = 0;
        foreach (var point in report.Trend.Monthly)
        {
            TsDataRow(ws, row, lastCol, monthIndex % 2 == 1);
            ws.Cell(row, 1).Value = point.Label;
            ws.Cell(row, 2).Value = point.IsPartial ? "yes" : "";
            ws.Cell(row, 3).Value = point.CalendarDayCount;
            ws.Cell(row, 4).Value = point.ActiveDayCount;
            ws.Cell(row, 5).Value = point.DocumentCount;
            ws.Cell(row, 6).Value = point.ProductiveCalls;
            ws.Cell(row, 7).Value = MoneyText(point.TotalsByCurrency);
            row++;
            monthIndex++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "DAY BY DAY — SILENT DAYS INCLUDED");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Date", "Day", "Reps Out", "Documents", "Productive Calls", "Takings", ""]);

        int dayIndex = 0;
        foreach (var point in report.Trend.Daily)
        {
            TsDataRow(ws, row, lastCol, dayIndex % 2 == 1);
            WriteVanPerformanceDate(ws.Cell(row, 1), point.TradingDate);
            ws.Cell(row, 2).Value = point.DayOfWeek.ToString();
            ws.Cell(row, 3).Value = point.RepsTrading;
            ws.Cell(row, 4).Value = point.DocumentCount;
            ws.Cell(row, 5).Value = point.ProductiveCalls;
            ws.Cell(row, 6).Value = MoneyText(point.TotalsByCurrency);
            row++;
            dayIndex++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildVanPerformanceDropSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        DateTime now)
    {
        const int lastCol = 6;
        var ws = workbook.Worksheets.Add("Drop Size");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "DROP SIZE DISTRIBUTION", lastCol, now);

        ws.Cell(row, 1).Value =
            "A drop is one shop, on one day, in one currency — two invoices at the same counter "
            + "are one drop. A median well under the mean means a few large accounts are carrying a "
            + "long tail of calls that barely pay for the stop.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 24;
        row += 2;

        foreach (var distribution in report.DropSizes)
        {
            TsSectionTitle(ws, row, lastCol, distribution.Currency.ToUpperInvariant());
            row += 2;

            row = TsKpiStrip(ws, row, lastCol,
                ("Drops", distribution.DropCount.ToString("N0"), null),
                ("Mean", distribution.Mean.ToString("N2"), null),
                ("Median", distribution.Median.ToString("N2"), null),
                ("Lower Quartile", distribution.P25.ToString("N2"), null),
                ("Upper Quartile", distribution.P75.ToString("N2"), null),
                ("Largest", distribution.Maximum.ToString("N2"), null));

            row = TsColumnHeaders(ws, row, lastCol,
                ["Band", "Drops", "Share %", "Value", "", ""]);

            int index = 0;
            foreach (var bucket in distribution.Buckets)
            {
                TsDataRow(ws, row, lastCol, index % 2 == 1);
                ws.Cell(row, 1).Value = bucket.Label;
                ws.Cell(row, 2).Value = bucket.DropCount;

                // This sheet is per currency by construction, so its money is a real number and a
                // reader may total the column safely.
                ws.Cell(row, 3).Value = bucket.SharePercent ?? 0;
                ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.0";
                ws.Cell(row, 4).Value = bucket.Total;
                ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                row++;
                index++;
            }

            row++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    /// <summary>
    /// Money as text, one entry per currency. Text rather than a number on purpose: a numeric column
    /// carrying two currencies invites a total that describes nothing.
    /// </summary>
    private static string MoneyText(List<VanSalesMoney> totals) =>
        totals.Count == 0
            ? "—"
            : string.Join("  |  ", totals.Select(total => $"{total.Currency} {total.Gross:N2}"));

    private static string LineMoneyText(List<VanSalesLineMoney> totals) =>
        totals.Count == 0
            ? "—"
            : string.Join("  |  ", totals.Select(total => $"{total.Currency} {total.Gross:N2}"));

    /// <summary>Quantity against its unit, never summed across units.</summary>
    private static string QuantityText(List<VanSalesQuantity> quantities) =>
        quantities.Count == 0
            ? "—"
            : string.Join("  |  ", quantities.Select(q => $"{q.Quantity:N0} {q.DisplayUoM}"));

    /// <summary>An em dash for a rate with no denominator. Never 0%.</summary>
    private static string RateText(double? rate) => rate is { } value ? value.ToString("P0") : "—";

    /// <summary>
    /// A real date cell, so Excel sorts and filters it as one. The shared <c>WriteDateCell</c> parses
    /// a string; these DTOs already carry a <c>DateTime</c>, so there is nothing to parse.
    /// </summary>
    private static void WriteVanPerformanceDate(IXLCell cell, DateTime date)
    {
        cell.Value = date.Date;
        cell.Style.NumberFormat.Format = FormatDate;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Van sales coverage
    // ═══════════════════════════════════════════════════════════════
    //
    // Same ramp and the same money rule as the performance workbook beside it. Two things this one
    // has to carry that the other does not, both because a workbook gets forwarded and whoever opens
    // it second never saw the page:
    //
    //  - The uncovered register is measured against today's roster, not the roster as it stood.
    //  - The location sheet is not a geofence, and the figures on it cannot be read as one.
    //
    // Both are written onto their own sheets rather than only into the caveats block.

    public byte[] ExportVanSalesCoverageToExcel(VanSalesCoverageReportResponse report)
    {
        using var workbook = NewWorkbook("Van Sales Coverage");
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildCoverageOverviewSheet(workbook, report, now);
        BuildCoverageRepSheet(workbook, report, now);
        BuildCoverageUncoveredSheet(workbook, report, now);
        BuildCoverageChurnSheet(workbook, report, now);
        BuildCoverageConcentrationSheet(workbook, report, now);
        BuildCoverageOutletSheet(workbook, report, now);
        BuildCoverageLocationSheet(workbook, report, now);

        return WorkbookToBytes(workbook);
    }

    private static void BuildCoverageOverviewSheet(
        XLWorkbook workbook,
        VanSalesCoverageReportResponse report,
        DateTime now)
    {
        const int lastCol = 8;
        var ws = workbook.Worksheets.Add("Overview");
        TsApplyDefaults(ws);

        var period = $"VAN SALES COVERAGE  —  {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}";
        int row = TsTitleBar(ws, period, lastCol, now);

        var summary = report.Summary;

        row = TsKpiStrip(ws, row, lastCol,
            ("Shops On Books", summary.RosterSize?.ToString("N0") ?? "—", null),
            ("Called On", summary.OutletsVisited.ToString("N0"), null),
            ("Roster Reached", RateText(summary.RosterCoverageRate), null),
            ("Strike Rate", RateText(summary.StrikeRate), null),
            ("New Outlets", summary.NewOutlets.ToString("N0"), TsGreen),
            ("Lapsed Outlets", summary.LapsedOutlets.ToString("N0"), TsRed),
            ("Returned", summary.ReactivatedOutlets.ToString("N0"), null),
            ("Real GPS Fixes", RateText(report.LocationIntegrity.GpsFixRate), null));

        TsSectionTitle(ws, row, lastCol, "WHAT THIS PERIOD COULD NOT ANSWER");
        row += 2;

        foreach (var caveat in report.Quality.Caveats)
        {
            ws.Cell(row, 1).Value = caveat;
            ws.Range(row, 1, row, lastCol).Merge();
            ws.Cell(row, 1).Style.Font.FontSize = 9;
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
            ws.Cell(row, 1).Style.Alignment.WrapText = true;
            ws.Row(row).Height = 24;
            row++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol,
            $"RATES OVER TIME  —  LAPSED AFTER {report.LapseDays} DAYS WITHOUT BUYING");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Period", "Reps", "Planned", "Called", "Bought", "Compliance", "Strike Rate", "Shops Bought"]);

        int index = 0;
        foreach (var point in report.Trend)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = point.IsPartial ? $"{point.Label} (part)" : point.Label;
            ws.Cell(row, 2).Value = point.RepsTrading;
            ws.Cell(row, 3).Value = point.PlannedCalls?.ToString("N0") ?? "—";
            ws.Cell(row, 4).Value = point.Calls?.ToString("N0") ?? "—";
            ws.Cell(row, 5).Value = point.ProductiveCalls;
            ws.Cell(row, 6).Value = RateText(point.CallComplianceRate);
            ws.Cell(row, 7).Value = RateText(point.ProductiveCallRate);
            ws.Cell(row, 8).Value = point.OutletsBought;
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildCoverageRepSheet(
        XLWorkbook workbook,
        VanSalesCoverageReportResponse report,
        DateTime now)
    {
        const int lastCol = 11;
        var ws = workbook.Worksheets.Add("Reps");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "COVERAGE BY REP", lastCol, now);
        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Rep", "Account", "Shop Codes?", "Roster", "Reached", "Coverage",
            "Strike Rate", "Bought", "Missed", "Km", "Per Km"
        ]);

        int index = 0;
        foreach (var rep in report.Reps)
        {
            var efficiency = rep.EfficiencyByCurrency.FirstOrDefault();

            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = rep.DisplayName;
            ws.Cell(row, 2).Value = rep.VanAccountCode ?? "—";
            // Not a gap in the rep's work: a gap in what their sales record.
            ws.Cell(row, 3).Value = rep.OutletsAttributable ? "yes" : "no shop codes";
            ws.Cell(row, 4).Value = rep.RosterSize?.ToString("N0") ?? "—";
            ws.Cell(row, 5).Value = rep.OutletsVisited?.ToString("N0") ?? "—";
            ws.Cell(row, 6).Value = RateText(rep.RosterCoverageRate);
            ws.Cell(row, 7).Value = RateText(rep.StrikeRate);
            ws.Cell(row, 8).Value = rep.OutletsBought?.ToString("N0") ?? "—";
            ws.Cell(row, 9).Value = rep.OutletsUncovered?.ToString("N0") ?? "—";
            ws.Cell(row, 10).Value = rep.KilometresTravelled?.ToString("N0") ?? "—";
            ws.Cell(row, 11).Value = efficiency?.GrossPerKilometre is { } perKm
                ? $"{efficiency.Currency} {perKm:N2}"
                : "—";
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildCoverageUncoveredSheet(
        XLWorkbook workbook,
        VanSalesCoverageReportResponse report,
        DateTime now)
    {
        const int lastCol = 8;
        var ws = workbook.Worksheets.Add("Not Reached");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "SHOPS THE PERIOD DID NOT REACH", lastCol, now);

        ws.Cell(row, 1).Value =
            "Measured against TODAY'S roster, not the roster as it stood during the period. The day "
            + "plan is stored as a count and the list behind it was never kept, so this cannot say which "
            + "shops were planned for a given day — only which are on the books now and were not reached. "
            + "A shop added since reads as uncovered throughout; one removed since is absent entirely.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 34;
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
            ["Shop", "Code", "Account", "Gap", "Last Called On", "Last Bought", "Days", "Phone"]);

        int index = 0;
        foreach (var outlet in report.UncoveredOutlets)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = outlet.DisplayName;
            ws.Cell(row, 2).Value = outlet.OutletCode;
            ws.Cell(row, 3).Value = outlet.VanAccountCode;
            ws.Cell(row, 4).Value = outlet.GapLabel;

            if (outlet.LastVisitedOn is { } visited)
            {
                WriteVanPerformanceDate(ws.Cell(row, 5), visited);
            }
            else
            {
                ws.Cell(row, 5).Value = "—";
            }

            // Never bought and long lapsed are different conversations.
            if (outlet.HasNeverBought)
            {
                ws.Cell(row, 6).Value = "never";
            }
            else
            {
                WriteVanPerformanceDate(ws.Cell(row, 6), outlet.LastPurchaseOn!.Value);
            }

            ws.Cell(row, 7).Value = outlet.DaysSinceLastPurchase?.ToString("N0") ?? "—";
            ws.Cell(row, 8).Value = outlet.Phone ?? "—";
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildCoverageChurnSheet(
        XLWorkbook workbook,
        VanSalesCoverageReportResponse report,
        DateTime now)
    {
        const int lastCol = 9;
        var ws = workbook.Worksheets.Add("Churn");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "THE OUTLET BASE, PERIOD BY PERIOD", lastCol, now);

        TsSectionTitle(ws, row, lastCol,
            $"LAPSED AFTER {report.LapseDays} DAYS  —  COUNTED IN THE PERIOD THE LINE WAS CROSSED");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Period", "Opening", "New", "Returned", "Lapsed", "Closing", "Net", "Churn", "Residual"]);

        int index = 0;
        foreach (var point in report.Churn)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = point.IsCensored
                ? $"{point.Label} (before the data starts)"
                : point.IsPartial ? $"{point.Label} (part)" : point.Label;
            ws.Cell(row, 2).Value = point.OpeningActiveOutlets;
            ws.Cell(row, 3).Value = point.NewOutlets;
            ws.Cell(row, 4).Value = point.ReactivatedOutlets;
            ws.Cell(row, 5).Value = point.LapsedOutlets;
            ws.Cell(row, 6).Value = point.ClosingActiveOutlets;
            ws.Cell(row, 7).Value = point.NetMovement;
            ws.Cell(row, 8).Value = RateText(point.ChurnRate);

            // Should always be zero. Written out so a fault is visible in the workbook too.
            ws.Cell(row, 9).Value = point.UnexplainedMovement;
            if (point.UnexplainedMovement != 0)
            {
                ws.Cell(row, 9).Style.Font.FontColor = TsRed;
                ws.Cell(row, 9).Style.Font.Bold = true;
            }

            row++;
            index++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "WIN-BACK LIST  —  BIGGEST LOSS FIRST");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Shop", "Code", "Last Bought", "Days Ago", "Buying Days",
            "Last Drop", "Worth Before", "Sold By", "On Roster?"
        ]);

        int lapsedIndex = 0;
        foreach (var outlet in report.LapsedOutlets)
        {
            TsDataRow(ws, row, lastCol, lapsedIndex % 2 == 1);
            ws.Cell(row, 1).Value = outlet.DisplayName;
            ws.Cell(row, 2).Value = outlet.OutletCode;
            WriteVanPerformanceDate(ws.Cell(row, 3), outlet.LastPurchaseOn);
            ws.Cell(row, 4).Value = outlet.DaysSinceLastPurchase;
            ws.Cell(row, 5).Value = outlet.PriorPurchaseDayCount;
            ws.Cell(row, 6).Value = MoneyText(outlet.LastPurchaseByCurrency);
            ws.Cell(row, 7).Value = MoneyText(outlet.PriorTotalsByCurrency);
            ws.Cell(row, 8).Value = outlet.LastSoldByRep ?? "—";
            ws.Cell(row, 9).Value = outlet.StillOnRoster ? "yes" : "dropped";
            row++;
            lapsedIndex++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildCoverageConcentrationSheet(
        XLWorkbook workbook,
        VanSalesCoverageReportResponse report,
        DateTime now)
    {
        const int lastCol = 9;
        var ws = workbook.Worksheets.Add("Concentration");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "WHAT EACH ROUTE RESTS ON", lastCol, now);
        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Route", "Currency", "Shops", "Half The Takings", "Top Shop %",
            "Top 5 %", "Top 10 %", "Unattributed %", "Largest Shop"
        ]);

        int index = 0;
        foreach (var route in report.Concentration)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = route.DisplayRoute;
            ws.Cell(row, 2).Value = route.Currency;
            ws.Cell(row, 3).Value = route.OutletCount;
            ws.Cell(row, 4).Value = route.OutletsForHalfOfGross?.ToString("N0") ?? "—";
            ws.Cell(row, 5).Value = PercentText(route.Top1SharePercent);
            ws.Cell(row, 6).Value = PercentText(route.Top5SharePercent);
            ws.Cell(row, 7).Value = PercentText(route.Top10SharePercent);
            ws.Cell(row, 8).Value = PercentText(route.UnattributedSharePercent);
            ws.Cell(row, 9).Value = route.TopOutlets.FirstOrDefault()?.DisplayName ?? "—";
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildCoverageOutletSheet(
        XLWorkbook workbook,
        VanSalesCoverageReportResponse report,
        DateTime now)
    {
        const int lastCol = 9;
        var ws = workbook.Worksheets.Add("Shop Behaviour");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "HOW EACH SHOP BUYS", lastCol, now);
        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Shop", "Code", "Called On", "Bought On", "Conversion",
            "Items", "Per Drop", "Days Between", "Takings"
        ]);

        int index = 0;
        foreach (var outlet in report.Outlets)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = outlet.DisplayName;
            ws.Cell(row, 2).Value = outlet.OutletCode;
            ws.Cell(row, 3).Value = outlet.VisitCount?.ToString("N0") ?? "—";
            ws.Cell(row, 4).Value = outlet.PurchaseDayCount;
            ws.Cell(row, 5).Value = RateText(outlet.ConversionRate);
            ws.Cell(row, 6).Value = outlet.DistinctItemCount;
            ws.Cell(row, 7).Value = outlet.AverageItemsPerPurchase;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.0";
            ws.Cell(row, 8).Value = outlet.AverageDaysBetweenPurchases is { } gap
                ? gap.ToString("N1")
                : "—";
            ws.Cell(row, 9).Value = MoneyText(outlet.TotalsByCurrency);
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildCoverageLocationSheet(
        XLWorkbook workbook,
        VanSalesCoverageReportResponse report,
        DateTime now)
    {
        const int lastCol = 8;
        var ws = workbook.Worksheets.Add("Location");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "LOCATION RECORD", lastCol, now);

        ws.Cell(row, 1).Value =
            "THIS IS NOT A GEOFENCE. Shops have no recorded coordinates anywhere in this system, so "
            + "whether a rep was at the door cannot be answered at all. What follows is whether the "
            + $"position on a call was measured by GPS at the moment, only remembered from earlier, or "
            + $"absent — and a fix vaguer than {report.LocationIntegrity.PoorAccuracyMetres} m is counted "
            + "as too imprecise to place anyone.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 34;
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
            ["Rep", "Calls", "Real Fix", "Remembered", "None", "Too Vague", "Queued Offline", "Fix Rate"]);

        int index = 0;
        foreach (var rep in report.LocationIntegrity.Reps)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = rep.DisplayName;
            ws.Cell(row, 2).Value = rep.CallCount;
            ws.Cell(row, 3).Value = rep.CallsWithGpsFix;
            ws.Cell(row, 4).Value = rep.CallsWithLastKnownFix;
            ws.Cell(row, 5).Value = rep.CallsWithNoFix;
            ws.Cell(row, 6).Value = rep.CallsWithPoorAccuracy;
            ws.Cell(row, 7).Value = rep.CallsCapturedOffline;
            ws.Cell(row, 8).Value = RateText(rep.GpsFixRate);
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    /// <summary>A percentage with an em dash for the undefined case. Never 0%.</summary>
    private static string PercentText(double? share) => share is { } value ? $"{value:N1}%" : "—";

    // ═══════════════════════════════════════════════════════════════
    //  Van replenishment
    // ═══════════════════════════════════════════════════════════════

    public byte[] ExportVanReplenishmentToExcel(VanReplenishmentReportResponse report)
    {
        using var workbook = NewWorkbook("Van Replenishment");
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildReplenishmentWorklistSheet(workbook, report, now);
        BuildReplenishmentVanSheet(workbook, report, now);

        return WorkbookToBytes(workbook);
    }

    /// <summary>
    /// The worklist leads the workbook as it leads the page. Everything else here records how things
    /// have been going; this is the sheet somebody acts on, and an approved transfer that never
    /// reached SAP is surfaced nowhere else in the system.
    /// </summary>
    private static void BuildReplenishmentWorklistSheet(
        XLWorkbook workbook,
        VanReplenishmentReportResponse report,
        DateTime now)
    {
        const int lastCol = 8;
        var ws = workbook.Worksheets.Add("Stuck Now");
        TsApplyDefaults(ws);

        var period = $"VAN REPLENISHMENT  —  {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}";
        int row = TsTitleBar(ws, period, lastCol, now);

        var summary = report.Summary;

        row = TsKpiStrip(ws, row, lastCol,
            ("Requests", summary.RequestCount.ToString("N0"), null),
            ("Reached SAP", RateText(summary.PostRate), null),
            ("Wait To Decide", HoursText(summary.MedianHoursToDecision), null),
            ("Wait To Post", HoursText(summary.MedianHoursToPosting), null),
            ("Needing Attention", summary.NeedingAttentionCount.ToString("N0"),
                summary.NeedingAttentionCount > 0 ? TsRed : null),
            ("Failed To Post", summary.PostFailedCount.ToString("N0"),
                summary.PostFailedCount > 0 ? TsRed : null),
            ("Rejected", summary.RejectedCount.ToString("N0"), null),
            ("Vans", summary.VanCount.ToString("N0"), null));

        if (!report.Quality.IsClean)
        {
            TsSectionTitle(ws, row, lastCol, "WHAT THIS PERIOD COULD NOT ANSWER");
            row += 2;

            foreach (var caveat in report.Quality.Caveats)
            {
                ws.Cell(row, 1).Value = caveat;
                ws.Range(row, 1, row, lastCol).Merge();
                ws.Cell(row, 1).Style.Font.FontSize = 9;
                ws.Cell(row, 1).Style.Font.Italic = true;
                ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
                ws.Cell(row, 1).Style.Alignment.WrapText = true;
                ws.Row(row).Height = 24;
                row++;
            }

            row++;
        }

        TsSectionTitle(ws, row, lastCol, "REQUESTS THAT HAVE NOT REACHED SAP");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Van", "From Depot", "Problem", "Asked By", "Asked", "Waiting", "Lines", "Last Error"]);

        int index = 0;
        foreach (var request in report.NeedingAttention)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = request.VanWarehouseCode;
            ws.Cell(row, 2).Value = request.DepotWarehouseCode;
            ws.Cell(row, 3).Value = request.GapLabel;
            ws.Cell(row, 4).Value = request.RequestedBy;
            WriteVanPerformanceDate(ws.Cell(row, 5), request.RequestedAt);
            ws.Cell(row, 6).Value = request.DaysWaiting >= 1
                ? $"{request.DaysWaiting:N0}d"
                : $"{request.HoursWaiting:N0}h";
            ws.Cell(row, 7).Value = request.LineCount;
            ws.Cell(row, 8).Value = request.LastError ?? "—";

            if (request.IsPostFailure)
            {
                ws.Cell(row, 3).Style.Font.FontColor = TsRed;
                ws.Cell(row, 3).Style.Font.Bold = true;
            }

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildReplenishmentVanSheet(
        XLWorkbook workbook,
        VanReplenishmentReportResponse report,
        DateTime now)
    {
        const int lastCol = 10;
        var ws = workbook.Worksheets.Add("By Van");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "SERVICE LEVEL BY VAN", lastCol, now);
        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Van", "Depots", "Requests", "Posted", "Rejected", "Stuck",
            "To Decide", "To Post", "Slowest", "Last Supplied"
        ]);

        int index = 0;
        foreach (var van in report.Vans)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = van.VanWarehouseCode;
            ws.Cell(row, 2).Value = van.DepotWarehouses.Count == 0
                ? "—"
                : string.Join(", ", van.DepotWarehouses);
            ws.Cell(row, 3).Value = van.RequestCount;
            ws.Cell(row, 4).Value = van.PostedCount;
            ws.Cell(row, 5).Value = van.RejectedCount;
            ws.Cell(row, 6).Value = van.NeedingAttentionCount;
            ws.Cell(row, 7).Value = HoursText(van.MedianHoursToDecision);
            ws.Cell(row, 8).Value = HoursText(van.MedianHoursToPosting);
            ws.Cell(row, 9).Value = HoursText(van.SlowestHoursToPosting);

            // Never supplied is a different finding from supplied a long time ago.
            ws.Cell(row, 10).Value = van.LastPostedAt is { } posted
                ? $"{posted:dd MMM} ({van.DaysSinceLastPosted:N0}d ago)"
                : "never";

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Van stock
    // ═══════════════════════════════════════════════════════════════

    public byte[] ExportVanStockToExcel(VanStockReportResponse report)
    {
        using var workbook = NewWorkbook("Van Stock");
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildVanStockOverviewSheet(workbook, report, now);
        BuildVanStockVarianceSheet(workbook, report, now);
        BuildVanStockDaySheet(workbook, report, now);
        BuildVanStockItemSheet(workbook, report, now);
        BuildVanStockExpirySheet(workbook, report, now);

        return WorkbookToBytes(workbook);
    }

    private static void BuildVanStockOverviewSheet(
        XLWorkbook workbook,
        VanStockReportResponse report,
        DateTime now)
    {
        const int lastCol = 6;
        var ws = workbook.Worksheets.Add("Overview");
        TsApplyDefaults(ws);

        var period = $"VAN STOCK  —  {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}";
        int row = TsTitleBar(ws, period, lastCol, now);

        // Ahead of every figure it governs. If the snapshot job stops, everything below describes an
        // older morning and nothing else in the workbook would say so.
        if (report.Summary.IsStale)
        {
            ws.Cell(row, 1).Value =
                $"THESE FIGURES ARE {report.Summary.SnapshotAgeDays:N0} DAY(S) OLD. The newest stock "
                + $"snapshot is from {report.Summary.LatestSnapshotDate:dddd dd MMM yyyy}, so everything "
                + "below describes the vans as they stood then. If the snapshot job has stopped, "
                + "nothing here will improve until it runs again.";
            ws.Range(row, 1, row, lastCol).Merge();
            ws.Cell(row, 1).Style.Font.FontSize = 10;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontColor = TsRed;
            ws.Cell(row, 1).Style.Alignment.WrapText = true;
            ws.Row(row).Height = 36;
            row += 2;
        }

        var summary = report.Summary;

        row = TsKpiStrip(ws, row, lastCol,
            ("Vans", summary.VanCount.ToString("N0"), null),
            ("Sell-Through", RateText(summary.SellThroughRate), null),
            ("Dead Lines", summary.DeadItemCount.ToString("N0"),
                summary.DeadItemCount > 0 ? TsOrange : null),
            ("Items Seen", summary.ItemCount.ToString("N0"), null),
            ("Van-Days", summary.SnapshotDayCount.ToString("N0"), null),
            ("Missing Days", summary.MissingSnapshotDays.ToString("N0"),
                summary.MissingSnapshotDays > 0 ? TsOrange : null));

        if (!report.Quality.IsClean)
        {
            TsSectionTitle(ws, row, lastCol, "WHAT THIS PERIOD COULD NOT ANSWER");
            row += 2;

            foreach (var caveat in report.Quality.Caveats)
            {
                ws.Cell(row, 1).Value = caveat;
                ws.Range(row, 1, row, lastCol).Merge();
                ws.Cell(row, 1).Style.Font.FontSize = 9;
                ws.Cell(row, 1).Style.Font.Italic = true;
                ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
                ws.Cell(row, 1).Style.Alignment.WrapText = true;
                ws.Row(row).Height = 26;
                row++;
            }
        }

        TsFinalize(ws, lastCol, freezeRow: 2);
    }

    private static void BuildVanStockVarianceSheet(
        XLWorkbook workbook,
        VanStockReportResponse report,
        DateTime now)
    {
        const int lastCol = 10;
        var ws = workbook.Worksheets.Add("Reconciliation");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "MORNING TO MORNING", lastCol, now);

        ws.Cell(row, 1).Value =
            "Yesterday's load, less what sold off it, plus anything that arrived, is what this morning "
            + "should have found. Only computable across two CONSECUTIVE snapshots — where a day is "
            + "missing the pair is shown as a break rather than reached over, because two days of "
            + "difference reported as one reads as a single large discrepancy on the wrong date.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 32;
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Van", "From", "To", "Gap", "Opened", "Sold", "Arrived", "Expected", "Found", "Variance"
        ]);

        int index = 0;
        foreach (var variance in report.Variances)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = variance.VanWarehouseCode;
            WriteVanPerformanceDate(ws.Cell(row, 2), variance.FromSnapshot);
            WriteVanPerformanceDate(ws.Cell(row, 3), variance.ToSnapshot);
            ws.Cell(row, 4).Value = variance.HasGap ? $"{variance.GapDays:N0} days" : "";
            ws.Cell(row, 5).Value = variance.OpeningQuantity;
            ws.Cell(row, 6).Value = variance.SoldQuantity;
            ws.Cell(row, 7).Value = variance.AdjustmentQuantity;

            // Across a gap there is nothing to expect and nothing to compare, so both read as
            // unavailable rather than as a zero difference.
            if (variance.ExpectedQuantity is { } expected)
            {
                ws.Cell(row, 8).Value = expected;
                ws.Cell(row, 9).Value = variance.ClosingQuantity;
                ws.Cell(row, 10).Value = variance.Variance!.Value;

                if (variance.Variance < 0)
                {
                    ws.Cell(row, 10).Style.Font.FontColor = TsRed;
                    ws.Cell(row, 10).Style.Font.Bold = true;
                }
            }
            else
            {
                ws.Cell(row, 8).Value = "—";
                ws.Cell(row, 9).Value = variance.ClosingQuantity;
                ws.Cell(row, 10).Value = "—";
            }

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildVanStockDaySheet(
        XLWorkbook workbook,
        VanStockReportResponse report,
        DateTime now)
    {
        const int lastCol = 9;
        var ws = workbook.Worksheets.Add("Load & Sell-Through");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "LOAD AND SELL-THROUGH, DAY BY DAY", lastCol, now);

        ws.Cell(row, 1).Value =
            "The load is the morning snapshot; what sold comes from the sales themselves, because the "
            + "snapshot's running quantity is never decremented by a van sale.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 24;
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Van", "Morning", "Complete", "Items", "Loaded", "Sold", "Arrived",
            "Should Be Left", "Sell-Through"
        ]);

        int index = 0;
        foreach (var day in report.Days)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = day.VanWarehouseCode;
            WriteVanPerformanceDate(ws.Cell(row, 2), day.SnapshotDate);
            ws.Cell(row, 3).Value = day.SnapshotComplete ? "yes" : "incomplete";
            ws.Cell(row, 4).Value = day.ItemCount;
            ws.Cell(row, 5).Value = day.LoadedQuantity;
            ws.Cell(row, 6).Value = day.SoldQuantity;
            ws.Cell(row, 7).Value = day.AdjustmentQuantity;
            ws.Cell(row, 8).Value = day.ExpectedRemaining;
            ws.Cell(row, 9).Value = RateText(day.SellThroughRate);

            if (day.SoldBeyondLoad)
            {
                ws.Cell(row, 8).Style.Font.FontColor = TsOrange;
            }

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildVanStockItemSheet(
        XLWorkbook workbook,
        VanStockReportResponse report,
        DateTime now)
    {
        const int lastCol = 9;
        var ws = workbook.Worksheets.Add("What Is Worth Carrying");
        TsApplyDefaults(ws);

        int row = TsTitleBar(
            ws, $"DEADEST FIRST  —  DEAD AFTER {report.DeadStockDays} DAYS UNSOLD", lastCol, now);

        row = TsColumnHeaders(ws, row, lastCol,
        [
            "Item Code", "Description", "Vans", "Days Carried", "Days Sold",
            "Days Idle", "Loaded", "Sold", "Sell-Through"
        ]);

        int index = 0;
        foreach (var item in report.Items)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = item.ItemCode;
            ws.Cell(row, 2).Value = item.ItemDescription ?? "—";
            ws.Cell(row, 3).Value = item.VanCount;
            ws.Cell(row, 4).Value = item.DaysOnVan;
            ws.Cell(row, 5).Value = item.DaysSold;
            ws.Cell(row, 6).Value = item.DaysOnVanWithoutSelling;
            ws.Cell(row, 7).Value = item.LoadedQuantity;
            ws.Cell(row, 8).Value = item.SoldQuantity;
            ws.Cell(row, 9).Value = RateText(item.SellThroughRate);

            if (item.IsDead)
            {
                ws.Cell(row, 1).Style.Font.FontColor = TsRed;
                ws.Cell(row, 2).Style.Font.FontColor = TsRed;
            }

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    private static void BuildVanStockExpirySheet(
        XLWorkbook workbook,
        VanStockReportResponse report,
        DateTime now)
    {
        const int lastCol = 7;
        var ws = workbook.Worksheets.Add("Expiry");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "RUNNING OUT OF TIME", lastCol, now);

        ws.Cell(row, 1).Value =
            "Batches on the newest snapshot of each van, soonest first. Read from the latest snapshot "
            + "only, because this is a question about what is on the van now rather than what was on "
            + "it every morning of the period.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 26;
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
            ["Van", "Item Code", "Description", "Batch", "Expires", "Days Left", "Quantity"]);

        int index = 0;
        foreach (var batch in report.Expiring)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = batch.VanWarehouseCode;
            ws.Cell(row, 2).Value = batch.ItemCode;
            ws.Cell(row, 3).Value = batch.ItemDescription ?? "—";
            ws.Cell(row, 4).Value = batch.BatchNumber;
            WriteVanPerformanceDate(ws.Cell(row, 5), batch.ExpiryDate);
            ws.Cell(row, 6).Value = batch.HasExpired
                ? $"{-batch.DaysToExpiry:N0} past"
                : batch.DaysToExpiry.ToString("N0");
            ws.Cell(row, 7).Value = batch.Quantity;

            if (batch.HasExpired)
            {
                ws.Cell(row, 6).Style.Font.FontColor = TsRed;
                ws.Cell(row, 6).Style.Font.Bold = true;
            }

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
    }

    /// <summary>
    /// A wait, in the unit that reads best at its size. An em dash for null: a van that asked for
    /// nothing has no waiting time, which is not the same as having been served instantly.
    /// </summary>
    private static string HoursText(double? hours) => hours switch
    {
        null => "—",
        < 1 => "<1h",
        < 48 => $"{hours.Value:N0}h",
        _ => $"{hours.Value / 24:N1}d"
    };

    public byte[] ExportVanAttendanceReportToExcel(
        VanVisitReportResponse report,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        using var workbook = NewWorkbook("Van Attendance Report");
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildVanAttendanceOverviewSheet(workbook, report, fromDate, toDate, now);

        foreach (var rep in report.RepSummaries.OrderByDescending(r => r.TotalCalls))
            BuildVanAttendanceRepSheet(workbook, rep, now);

        return WorkbookToBytes(workbook);
    }

    private static void BuildVanAttendanceOverviewSheet(
        XLWorkbook workbook,
        VanVisitReportResponse report,
        DateTime? fromDate,
        DateTime? toDate,
        DateTime now)
    {
        // Ten columns, to carry the two measures the screen leads on: the round's route, and the
        // on-site share behind it. A workbook that dropped them would not be the report anyone had
        // just been reading.
        const int lastCol = 10;
        var ws = workbook.Worksheets.Add("Overview");
        TsApplyDefaults(ws);

        var period = fromDate.HasValue && toDate.HasValue
            ? $"VAN ATTENDANCE REPORT  —  {fromDate:dd MMM yyyy} to {toDate:dd MMM yyyy}"
            : "VAN ATTENDANCE REPORT";
        int row = TsTitleBar(ws, period, lastCol, now);

        var completionPct = report.TotalCalls > 0
            ? (double)report.CompletedCalls / report.TotalCalls * 100
            : 0;
        var pctColor = completionPct >= 80 ? TsGreen : completionPct >= 50 ? TsOrange : TsRed;

        var busiestDay = report.RepSummaries.SelectMany(r => r.Days)
            .GroupBy(d => d.Date)
            .Select(g => new { Date = g.Key, Calls = g.Sum(x => x.CallCount) })
            .OrderByDescending(x => x.Calls).FirstOrDefault();

        row = TsKpiStrip(ws, row, lastCol,
            ("Calls", report.TotalCalls.ToString("N0"), null),
            ("Completed", report.CompletedCalls.ToString("N0"), null),
            ("Never Checked Out", report.OpenCalls.ToString("N0"), report.OpenCalls > 0 ? TsOrange : null),
            ("On Site", FormatHoursExcel(report.TotalHours * 60), null),
            ("Avg per Call", FormatHoursExcel(report.AverageCallMinutes), null),
            ("Reps", report.RepSummaries.Count.ToString("N0"), null),
            ("Trading Days", report.TradingDays.ToString("N0"), null),
            ("Completion", $"{completionPct:F0}%", pctColor));

        TsSectionTitle(ws, row, lastCol, "REP PERFORMANCE");
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
            ["Rep", "Route", "Calls", "Completed", "Open", "Customers", "Days", "On Site",
             "Avg per Call", "On-Site Share"]);

        int idx = 0;
        foreach (var rep in report.RepSummaries.OrderByDescending(r => r.TotalCalls))
        {
            TsDataRow(ws, row, lastCol, idx % 2 == 1);

            ws.Cell(row, 1).Value = rep.DisplayName;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = VanRouteLabelExcel(rep);
            ws.Cell(row, 3).Value = rep.TotalCalls;
            ws.Cell(row, 4).Value = rep.CompletedCalls;
            ws.Cell(row, 5).Value = rep.OpenCalls;
            if (rep.OpenCalls > 0)
            {
                ws.Cell(row, 5).Style.Font.FontColor = TsOrange;
                ws.Cell(row, 5).Style.Font.Bold = true;
            }
            ws.Cell(row, 6).Value = rep.DistinctCustomers;
            ws.Cell(row, 7).Value = rep.TradingDays;
            ws.Cell(row, 8).Value = FormatHoursExcel(rep.TotalMinutes);
            ws.Cell(row, 9).Value = FormatHoursExcel(rep.AverageMinutesPerCall);
            ws.Cell(row, 10).Value = FormatShareExcel(rep.OnSiteShare);

            for (int c = 2; c <= lastCol; c++)
                ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++; idx++;
        }

        var overallClock = report.RepSummaries.Sum(r => r.ClockMinutes);

        TsSummaryRow(ws, row, lastCol);
        ws.Cell(row, 1).Value = $"TOTAL: {report.RepSummaries.Count} REPS";
        ws.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(row, 3).Value = report.TotalCalls;
        ws.Cell(row, 4).Value = report.CompletedCalls;
        ws.Cell(row, 5).Value = report.OpenCalls;
        ws.Cell(row, 6).Value = report.RepSummaries
            .SelectMany(r => r.Customers).Select(c => c.CustomerCode)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        ws.Cell(row, 7).Value = report.TradingDays;
        ws.Cell(row, 8).Value = FormatHoursExcel(report.TotalHours * 60);
        ws.Cell(row, 9).Value = FormatHoursExcel(report.AverageCallMinutes);
        ws.Cell(row, 10).Value = FormatShareExcel(
            overallClock > 0 ? report.TotalHours * 60 / overallClock : null);
        for (int c = 2; c <= lastCol; c++)
        {
            ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, c).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        row += 2;

        // ── Daily activity across every rep ──
        var dailyTotals = report.RepSummaries.SelectMany(r => r.Days)
            .GroupBy(d => d.Date)
            .Select(g => new
            {
                Date = g.Key,
                Reps = g.Count(),
                Calls = g.Sum(x => x.CallCount),
                Open = g.Sum(x => x.OpenCalls),
                TotalMinutes = g.Sum(x => x.TotalMinutes),
                // Summed per rep-day, so two reps out at the same time count as two hours of van
                // time rather than one. The span column below is the fleet's wall clock instead.
                ClockMinutes = g.Sum(x => x.ClockMinutes ?? 0),
                FirstCheckIn = g.Where(x => x.FirstCheckIn.HasValue).Min(x => x.FirstCheckIn),
                LastCheckOut = g.Where(x => x.LastCheckOut.HasValue).Max(x => x.LastCheckOut)
            })
            .OrderByDescending(d => d.Date).ToList();

        if (dailyTotals.Count > 0)
        {
            TsSectionTitle(ws, row, lastCol, "DAILY ACTIVITY");
            row += 2;

            row = TsColumnHeaders(ws, row, lastCol,
                ["Trading Day", "Day", "Reps Out", "Calls", "Open", "On Site", "First In", "Last Out",
                 "Span", "On-Site Share"]);

            idx = 0;
            foreach (var day in dailyTotals)
            {
                TsDataRow(ws, row, lastCol, idx % 2 == 1);

                // The trading day is already a CAT date — it is not converted again here. The two
                // instants beside it are UTC and are.
                ws.Cell(row, 1).Value = day.Date.ToString("dd MMM yyyy");
                ws.Cell(row, 2).Value = day.Date.ToString("ddd");
                ws.Cell(row, 3).Value = day.Reps;
                ws.Cell(row, 4).Value = day.Calls;
                ws.Cell(row, 5).Value = day.Open;
                if (day.Open > 0)
                {
                    ws.Cell(row, 5).Style.Font.FontColor = TsOrange;
                    ws.Cell(row, 5).Style.Font.Bold = true;
                }
                ws.Cell(row, 6).Value = FormatHoursExcel(day.TotalMinutes);
                ws.Cell(row, 7).Value = day.FirstCheckIn.HasValue
                    ? ToCatExcel(day.FirstCheckIn.Value).ToString("HH:mm")
                    : "—";
                ws.Cell(row, 8).Value = day.LastCheckOut.HasValue
                    ? ToCatExcel(day.LastCheckOut.Value).ToString("HH:mm")
                    : "—";
                ws.Cell(row, 9).Value = day.FirstCheckIn.HasValue && day.LastCheckOut.HasValue
                    ? FormatHoursExcel((day.LastCheckOut.Value - day.FirstCheckIn.Value).TotalMinutes)
                    : "—";
                ws.Cell(row, 10).Value = FormatShareExcel(
                    day.ClockMinutes > 0 ? day.TotalMinutes / day.ClockMinutes : null);

                for (int c = 2; c <= lastCol; c++)
                    ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                row++; idx++;
            }

            row += 2;
        }

        TsDisclaimerRow(ws, row, lastCol, now);
        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 1);
        ws.Column(1).Width = 30;
    }

    private static void BuildVanAttendanceRepSheet(
        XLWorkbook workbook,
        VanVisitReportRepSummary rep,
        DateTime now)
    {
        // Ten, so the daily breakdown carries the same columns the opened row does on screen — the
        // closing time and the day's on-site share, which are the two figures the whole page is
        // about and which this sheet used to leave behind.
        const int lastCol = 10;

        // Excel rejects a sheet name over 31 characters or carrying any of :\/?*[] — a rep whose
        // display name trips either would otherwise fail the whole workbook at save.
        var sheetName = rep.DisplayName.Length > 28 ? rep.DisplayName[..28] : rep.DisplayName;
        sheetName = string.Concat(sheetName.Select(c => ":\\/?*[]".Contains(c) ? '_' : c));

        var ws = workbook.Worksheets.Add(sheetName);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"VAN ATTENDANCE  —  {rep.DisplayName.ToUpper()}", lastCol, now);

        var pct = rep.CompletionRate is { } rate ? rate * 100 : 0;
        var pctColor = pct >= 80 ? TsGreen : pct >= 50 ? TsOrange : TsRed;

        row = TsKpiStrip(ws, row, lastCol,
            ("Route", VanRouteLabelExcel(rep), null),
            ("Calls", rep.TotalCalls.ToString("N0"), null),
            ("Completed", rep.CompletedCalls.ToString("N0"), null),
            ("Open", rep.OpenCalls.ToString("N0"), rep.OpenCalls > 0 ? TsOrange : null),
            ("On Site", FormatHoursExcel(rep.TotalMinutes), null),
            ("Avg per Call", FormatHoursExcel(rep.AverageMinutesPerCall), null),
            ("Customers", rep.DistinctCustomers.ToString("N0"), null),
            ("On-Site Share", FormatShareExcel(rep.OnSiteShare), null),
            ("Completion", rep.CompletionRate is null ? "—" : $"{pct:F0}%", pctColor));

        TsSectionTitle(ws, row, lastCol, "DAILY BREAKDOWN");
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
            ["Trading Day", "Day", "Route", "Calls", "Customers", "Open", "First In", "Last Out",
             "On Site", "On-Site Share"]);

        int idx = 0;
        foreach (var day in rep.Days.OrderByDescending(d => d.Date))
        {
            TsDataRow(ws, row, lastCol, idx % 2 == 1);

            ws.Cell(row, 1).Value = day.Date.ToString("dd MMM yyyy");
            ws.Cell(row, 2).Value = day.Date.ToString("ddd");
            // The route the round actually ran on that day, not the rep's current one — the day
            // carries its own snapshot for exactly this reason.
            ws.Cell(row, 3).Value = string.IsNullOrWhiteSpace(day.RouteName)
                ? string.IsNullOrWhiteSpace(day.RouteCode) ? "—" : day.RouteCode!
                : day.RouteName!;
            ws.Cell(row, 4).Value = day.CallCount;
            ws.Cell(row, 5).Value = day.DistinctCustomers;
            ws.Cell(row, 6).Value = day.OpenCalls;
            if (day.OpenCalls > 0)
            {
                ws.Cell(row, 6).Style.Font.FontColor = TsOrange;
                ws.Cell(row, 6).Style.Font.Bold = true;
            }
            ws.Cell(row, 7).Value = day.FirstCheckIn.HasValue
                ? ToCatExcel(day.FirstCheckIn.Value).ToString("HH:mm")
                : "—";
            // "open" rather than a dash when the day has calls but never closed: the difference
            // between a day nobody worked and a day nobody checked out of is the whole finding.
            ws.Cell(row, 8).Value = day.LastCheckOut.HasValue
                ? ToCatExcel(day.LastCheckOut.Value).ToString("HH:mm")
                : day.CallCount > 0 ? "open" : "—";
            ws.Cell(row, 9).Value = FormatHoursExcel(day.TotalMinutes);
            ws.Cell(row, 10).Value = FormatShareExcel(day.OnSiteShare);

            for (int c = 2; c <= lastCol; c++)
                ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++; idx++;
        }

        row += 2;

        TsSectionTitle(ws, row, lastCol, "CUSTOMER BREAKDOWN");
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
            ["Customer", "Code", "Calls", "On Site", "Avg per Call", "", "", "", "", ""]);

        idx = 0;
        foreach (var customer in rep.Customers.OrderByDescending(c => c.CallCount))
        {
            TsDataRow(ws, row, lastCol, idx % 2 == 1);

            ws.Cell(row, 1).Value = customer.CustomerName;
            ws.Cell(row, 2).Value = customer.CustomerCode;
            ws.Cell(row, 3).Value = customer.CallCount;
            ws.Cell(row, 4).Value = FormatHoursExcel(customer.TotalMinutes);
            ws.Cell(row, 5).Value = customer.CallCount > 0
                ? FormatHoursExcel(customer.TotalMinutes / customer.CallCount)
                : "—";

            for (int c = 2; c <= lastCol; c++)
                ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++; idx++;
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

    /// <summary>
    /// An on-site share for a cell, or an em dash when there is no clock behind it.
    ///
    /// Text rather than a percentage-formatted number, because the alternative to a figure here is
    /// not zero — it is nothing at all. A day that never closed has no time on the clock, and a
    /// numeric cell would have to write 0%, which reads as a rep who visited nobody. This is the
    /// same distinction the screen draws with `.vna-none`, and the compliance sheet with its CCR.
    /// </summary>
    private static string FormatShareExcel(double? share) =>
        share is { } value ? $"{value * 100:F0}%" : "—";

    /// <summary>
    /// The rep's round, or a stated absence. Matches the wording on the two van pages: a rep who
    /// never started a day on the handset has no route, and a blank cell reads as a broken export.
    /// </summary>
    private static string VanRouteLabelExcel(VanVisitReportRepSummary rep) =>
        !string.IsNullOrWhiteSpace(rep.RouteName) ? rep.RouteName!
        : !string.IsNullOrWhiteSpace(rep.RouteCode) ? rep.RouteCode!
        : "Route not recorded";

    private static DateTime ToCatExcel(DateTime utc) => utc.AddHours(2);

    private static bool TryParseReportDate(string? dateText, out DateTime parsed)
    {
        // Invariant first: these arrive from SAP as yyyy-MM-dd, and a machine set to a
        // day-first locale would otherwise read 2026-03-04 correctly but 03/04/2026
        // as the wrong day.
        if (DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            return true;

        return DateTime.TryParse(dateText, out parsed);
    }

    /// <summary>
    /// Writes a date that reached us as a string into a real date cell, so the column
    /// sorts chronologically and can be filtered to a range. Text that will not parse
    /// is kept verbatim rather than dropped — a value we cannot read is still evidence.
    /// </summary>
    private static void WriteDateCell(IXLCell cell, string? dateText, string format = FormatDate)
    {
        if (TryParseReportDate(dateText, out var parsed))
        {
            cell.Value = parsed.Date;
            cell.Style.NumberFormat.Format = format;
        }
        else
        {
            cell.Value = dateText ?? string.Empty;
        }
    }

    private static byte[] WorkbookToBytes(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// The item volume and customer revenue reports, which share one result, share
    /// one workbook: four sheets covering items, partners, periods and the document
    /// lines the totals were built from.
    /// </summary>
    /// <remarks>
    /// Every money column is a USD/ZiG pair rather than one figure. There is no rate
    /// on the document that would make a combined total honest, and a workbook is
    /// exactly where somebody would go on to sum a column.
    ///
    /// The volume columns are left empty — not zero — for an item with no conversion
    /// factor, so a SUM over the column is the same floor the page reports rather
    /// than a total that silently counted the unconvertible items as nothing.
    /// </remarks>
    public byte[] ExportItemVolumeSalesReportToExcel(GetItemVolumeSalesReportResult report, string title)
    {
        using var workbook = NewWorkbook(title);

        var itemsSheet = AddSheet(workbook, "Items");
        const int itemCols = 12;
        var row = WriteReportHeader(
            itemsSheet,
            $"{title} — by item",
            itemCols,
            report.FromDateUtc,
            report.ToDateUtc);

        WriteKpiCard(itemsSheet, row, 1, "Net Volume", report.Summary.NetVolume, FormatVolume);
        WriteKpiCard(itemsSheet, row, 2, "Net Quantity", report.Summary.NetQuantity, FormatQuantity);
        WriteKpiCard(itemsSheet, row, 3, "Net Revenue USD", report.Summary.NetRevenueUsd, FormatUsd);
        WriteKpiCard(itemsSheet, row, 4, "Net Revenue ZiG", report.Summary.NetRevenueZig, FormatZig);
        WriteKpiCard(itemsSheet, row, 5, "Invoices", report.Summary.InvoiceCount, FormatCount);
        WriteKpiCard(itemsSheet, row, 6, "Credit Notes", report.Summary.CreditNoteCount, FormatCount, WarningOrange);
        row += 3;

        if (report.Summary.ItemsWithoutFactorCount > 0)
        {
            itemsSheet.Range(row, 1, row, itemCols).Merge();
            itemsSheet.Cell(row, 1).Value =
                $"{report.Summary.ItemsWithoutFactorCount:N0} item(s) have no volume conversion factor, so " +
                $"{report.Summary.QuantityWithoutFactor:N2} units are excluded from every volume figure below: " +
                string.Join(", ", report.ItemCodesWithoutFactor);
            itemsSheet.Cell(row, 1).Style.Font.FontColor = WarningOrange;
            itemsSheet.Cell(row, 1).Style.Alignment.WrapText = true;
            itemsSheet.Row(row).Height = 30;
            row += 2;
        }

        var itemsHeader = row;
        row = WriteItemVolumeTable(
            itemsSheet,
            row,
            [
                "Item Code", "Item Name", "Factor", "Invoiced Qty", "Credited Qty", "Net Qty",
                "Net Volume", "Invoiced USD", "Invoiced ZiG", "Credited USD", "Credited ZiG", "Net Revenue USD"
            ]);

        var itemsStart = row;
        foreach (var item in report.ItemTotals)
        {
            itemsSheet.Cell(row, 1).Value = item.ItemCode;
            itemsSheet.Cell(row, 2).Value = item.ItemName;

            if (item.HasVolumeFactor)
            {
                itemsSheet.Cell(row, 3).Value = item.VolumeFactor!.Value;
                itemsSheet.Cell(row, 3).Style.NumberFormat.Format = "#,##0.######";
                itemsSheet.Cell(row, 7).Value = item.NetVolume;
                itemsSheet.Cell(row, 7).Style.NumberFormat.Format = FormatVolume;
            }
            else
            {
                itemsSheet.Cell(row, 3).Value = "no factor";
                itemsSheet.Cell(row, 3).Style.Font.FontColor = WarningOrange;
                itemsSheet.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }

            itemsSheet.Cell(row, 4).Value = item.InvoicedQuantity;
            itemsSheet.Cell(row, 5).Value = item.CreditedQuantity;
            itemsSheet.Cell(row, 6).Value = item.NetQuantity;
            itemsSheet.Cell(row, 8).Value = item.InvoicedSalesUsd;
            itemsSheet.Cell(row, 9).Value = item.InvoicedSalesZig;
            itemsSheet.Cell(row, 10).Value = item.CreditedSalesUsd;
            itemsSheet.Cell(row, 11).Value = item.CreditedSalesZig;
            itemsSheet.Cell(row, 12).Value = item.NetRevenueUsd;

            itemsSheet.Range(row, 4, row, 6).Style.NumberFormat.Format = FormatQuantity;
            itemsSheet.Cell(row, 8).Style.NumberFormat.Format = FormatUsd;
            itemsSheet.Cell(row, 9).Style.NumberFormat.Format = FormatZig;
            itemsSheet.Cell(row, 10).Style.NumberFormat.Format = FormatUsd;
            itemsSheet.Cell(row, 11).Style.NumberFormat.Format = FormatZig;
            itemsSheet.Cell(row, 12).Style.NumberFormat.Format = FormatUsd;

            row++;
        }

        var itemsLast = row - 1;
        row = FinishTable(itemsSheet, itemsHeader, itemsStart, row, itemCols, "No item sales fell in this period.");

        itemsSheet.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(itemsSheet, row, 4, itemsStart, itemsLast, FormatQuantity);
        WriteSubtotal(itemsSheet, row, 5, itemsStart, itemsLast, FormatQuantity);
        WriteSubtotal(itemsSheet, row, 6, itemsStart, itemsLast, FormatQuantity);
        WriteSubtotal(itemsSheet, row, 7, itemsStart, itemsLast, FormatVolume);
        WriteSubtotal(itemsSheet, row, 8, itemsStart, itemsLast, FormatUsd);
        WriteSubtotal(itemsSheet, row, 9, itemsStart, itemsLast, FormatZig);
        WriteSubtotal(itemsSheet, row, 10, itemsStart, itemsLast, FormatUsd);
        WriteSubtotal(itemsSheet, row, 11, itemsStart, itemsLast, FormatZig);
        WriteSubtotal(itemsSheet, row, 12, itemsStart, itemsLast, FormatUsd);
        StyleTotalsRow(itemsSheet, row, itemCols);

        WriteFooter(itemsSheet, row, itemCols);
        FinalizeSheet(itemsSheet, itemCols, itemsHeader, landscape: true);

        var partnersSheet = AddSheet(workbook, "Business Partners");
        const int partnerCols = 11;
        row = WriteReportHeader(
            partnersSheet,
            $"{title} — by business partner",
            partnerCols,
            report.FromDateUtc,
            report.ToDateUtc);

        var partnersHeader = row;
        row = WriteItemVolumeTable(
            partnersSheet,
            row,
            [
                "Card Code", "Card Name", "Invoices", "Credit Notes", "Invoiced Qty", "Credited Qty",
                "Net Qty", "Net Volume", "Net Revenue USD", "Net Revenue ZiG", "Items Without Factor"
            ]);

        var partnersStart = row;
        foreach (var account in report.AccountTotals
            .OrderByDescending(account => account.NetRevenueUsd + account.NetRevenueZig)
            .ThenBy(account => account.CardCode, StringComparer.OrdinalIgnoreCase))
        {
            partnersSheet.Cell(row, 1).Value = account.CardCode;
            partnersSheet.Cell(row, 2).Value = account.CardName;
            partnersSheet.Cell(row, 3).Value = account.InvoiceCount;
            partnersSheet.Cell(row, 4).Value = account.CreditNoteCount;
            partnersSheet.Cell(row, 5).Value = account.InvoicedQuantity;
            partnersSheet.Cell(row, 6).Value = account.CreditedQuantity;
            partnersSheet.Cell(row, 7).Value = account.NetQuantity;
            partnersSheet.Cell(row, 8).Value = account.NetVolume;
            partnersSheet.Cell(row, 9).Value = account.NetRevenueUsd;
            partnersSheet.Cell(row, 10).Value = account.NetRevenueZig;
            partnersSheet.Cell(row, 11).Value = account.ItemsWithoutFactorCount;

            partnersSheet.Range(row, 3, row, 4).Style.NumberFormat.Format = FormatCount;
            partnersSheet.Range(row, 3, row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            partnersSheet.Range(row, 5, row, 7).Style.NumberFormat.Format = FormatQuantity;
            partnersSheet.Cell(row, 8).Style.NumberFormat.Format = FormatVolume;
            partnersSheet.Cell(row, 9).Style.NumberFormat.Format = FormatUsd;
            partnersSheet.Cell(row, 10).Style.NumberFormat.Format = FormatZig;
            partnersSheet.Cell(row, 11).Style.NumberFormat.Format = FormatCount;
            partnersSheet.Cell(row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            if (account.ItemsWithoutFactorCount > 0)
            {
                partnersSheet.Cell(row, 11).Style.Font.FontColor = WarningOrange;
            }

            row++;
        }

        var partnersLast = row - 1;
        row = FinishTable(partnersSheet, partnersHeader, partnersStart, row, partnerCols, "No business partners traded in this period.");

        partnersSheet.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(partnersSheet, row, 3, partnersStart, partnersLast, FormatCount);
        partnersSheet.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(partnersSheet, row, 4, partnersStart, partnersLast, FormatCount);
        partnersSheet.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(partnersSheet, row, 5, partnersStart, partnersLast, FormatQuantity);
        WriteSubtotal(partnersSheet, row, 6, partnersStart, partnersLast, FormatQuantity);
        WriteSubtotal(partnersSheet, row, 7, partnersStart, partnersLast, FormatQuantity);
        WriteSubtotal(partnersSheet, row, 8, partnersStart, partnersLast, FormatVolume);
        WriteSubtotal(partnersSheet, row, 9, partnersStart, partnersLast, FormatUsd);
        WriteSubtotal(partnersSheet, row, 10, partnersStart, partnersLast, FormatZig);
        StyleTotalsRow(partnersSheet, row, partnerCols);

        WriteFooter(partnersSheet, row, partnerCols);
        FinalizeSheet(partnersSheet, partnerCols, partnersHeader, landscape: true);

        var periodsSheet = AddSheet(workbook, "Periods");
        const int periodCols = 7;
        row = WriteReportHeader(
            periodsSheet,
            $"{title} — by {report.Grouping.ToString().ToLowerInvariant()} period",
            periodCols,
            report.FromDateUtc,
            report.ToDateUtc);

        var periodsHeader = row;
        row = WriteItemVolumeTable(
            periodsSheet,
            row,
            ["Period", "Starts", "Invoices", "Credit Notes", "Net Qty", "Net Volume", "Net Revenue USD"]);

        var periodsStart = row;
        foreach (var period in report.Periods.OrderBy(period => period.PeriodStartUtc))
        {
            periodsSheet.Cell(row, 1).Value = period.Label;
            periodsSheet.Cell(row, 2).Value = period.PeriodStartUtc;
            periodsSheet.Cell(row, 2).Style.NumberFormat.Format = FormatDate;
            periodsSheet.Cell(row, 3).Value = period.InvoiceCount;
            periodsSheet.Cell(row, 4).Value = period.CreditNoteCount;
            periodsSheet.Cell(row, 5).Value = period.NetQuantity;
            periodsSheet.Cell(row, 6).Value = period.NetVolume;
            periodsSheet.Cell(row, 7).Value = period.NetRevenueUsd;

            periodsSheet.Range(row, 3, row, 4).Style.NumberFormat.Format = FormatCount;
            periodsSheet.Range(row, 3, row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            periodsSheet.Cell(row, 5).Style.NumberFormat.Format = FormatQuantity;
            periodsSheet.Cell(row, 6).Style.NumberFormat.Format = FormatVolume;
            periodsSheet.Cell(row, 7).Style.NumberFormat.Format = FormatUsd;

            row++;
        }

        var periodsLast = row - 1;
        row = FinishTable(periodsSheet, periodsHeader, periodsStart, row, periodCols, "No periods fell in this report's range.");

        periodsSheet.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(periodsSheet, row, 3, periodsStart, periodsLast, FormatCount);
        periodsSheet.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(periodsSheet, row, 4, periodsStart, periodsLast, FormatCount);
        periodsSheet.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        WriteSubtotal(periodsSheet, row, 5, periodsStart, periodsLast, FormatQuantity);
        WriteSubtotal(periodsSheet, row, 6, periodsStart, periodsLast, FormatVolume);
        WriteSubtotal(periodsSheet, row, 7, periodsStart, periodsLast, FormatUsd);
        StyleTotalsRow(periodsSheet, row, periodCols);

        WriteFooter(periodsSheet, row, periodCols);
        FinalizeSheet(periodsSheet, periodCols, periodsHeader);

        var linesSheet = AddSheet(workbook, "Document Lines");
        const int lineCols = 11;
        row = WriteReportHeader(
            linesSheet,
            $"{title} — document lines",
            lineCols,
            report.FromDateUtc,
            report.ToDateUtc);

        var linesHeader = row;
        row = WriteItemVolumeTable(
            linesSheet,
            row,
            [
                "Date", "Type", "Document", "Card Code", "Card Name", "Item Code", "Item Name",
                "Quantity", "Factor", "Volume", "Line Amount"
            ]);

        var linesStart = row;
        foreach (var line in report.DocumentLines)
        {
            linesSheet.Cell(row, 1).Value = line.DocumentDateUtc;
            linesSheet.Cell(row, 1).Style.NumberFormat.Format = FormatDate;
            linesSheet.Cell(row, 2).Value = line.DocumentType;
            linesSheet.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            linesSheet.Cell(row, 3).Value = line.DocumentNumber;
            linesSheet.Cell(row, 4).Value = line.CardCode;
            linesSheet.Cell(row, 5).Value = line.CardName;
            linesSheet.Cell(row, 6).Value = line.ItemCode;
            linesSheet.Cell(row, 7).Value = line.ItemName;
            linesSheet.Cell(row, 8).Value = line.Quantity;
            linesSheet.Cell(row, 8).Style.NumberFormat.Format = FormatQuantity;

            if (line.VolumeFactor.HasValue)
            {
                linesSheet.Cell(row, 9).Value = line.VolumeFactor.Value;
                linesSheet.Cell(row, 9).Style.NumberFormat.Format = "#,##0.######";
                linesSheet.Cell(row, 10).Value = line.Volume;
                linesSheet.Cell(row, 10).Style.NumberFormat.Format = FormatVolume;
            }

            linesSheet.Cell(row, 11).Value = line.LineAmount;
            linesSheet.Cell(row, 11).Style.NumberFormat.Format = $"\"{line.Currency}\" #,##0.00;[Red](\"{line.Currency}\" #,##0.00)";

            if (string.Equals(line.DocumentType, "Credit Note", StringComparison.OrdinalIgnoreCase))
            {
                linesSheet.Cell(row, 2).Style.Font.FontColor = DangerRed;
            }

            row++;
        }

        var linesLast = row - 1;
        row = FinishTable(linesSheet, linesHeader, linesStart, row, lineCols, "No document lines fell in this period.");

        // Quantity and volume only: the amounts on this sheet are each stated in their
        // own document's currency, which is why the column names one per cell.
        linesSheet.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(linesSheet, row, 8, linesStart, linesLast, FormatQuantity);
        WriteSubtotal(linesSheet, row, 10, linesStart, linesLast, FormatVolume);
        StyleTotalsRow(linesSheet, row, lineCols);

        WriteFooter(linesSheet, row, lineCols);
        FinalizeSheet(linesSheet, lineCols, linesHeader, landscape: true);

        return WorkbookToBytes(workbook);
    }

    private static int WriteItemVolumeTable(IXLWorksheet ws, int row, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }

        StyleTableHeader(ws, row, headers.Length);
        return row + 1;
    }

    public byte[] ExportAccountSalesPaymentReportToExcel(GetAccountSalesPaymentReportResult report)
    {
        using var workbook = NewWorkbook("Account Sales & Payment Report");

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
            "ACCOUNT SALES & PAYMENTS DASHBOARD",
            "KEFALOS CHEESE (PVT) LTD executive finance report for reconciliation, collections, and customer-level review.",
            report,
            15);
        TryAddExecutiveLogo(dashboard, logoPath, 2, 13, 0.14);

        WriteExecutiveKpiCard(dashboard, row, 1, 3, ExecutiveRoyalBlue, "TOTAL SALES", $"USD {report.Summary.TotalSalesUsd:N2}", $"ZiG {report.Summary.TotalSalesZig:N2}", "Gross invoiced value across the selected accounts.");
        WriteExecutiveKpiCard(dashboard, row, 4, 6, ExecutiveEmerald, "TOTAL PAYMENTS", $"USD {report.Summary.TotalIncomingPaymentsUsd:N2}", $"ZiG {report.Summary.TotalIncomingPaymentsZig:N2}", "Cash collections grouped by payment date.");
        WriteExecutiveKpiCard(dashboard, row, 7, 9, ExecutiveAmber, "OUTSTANDING BALANCE", $"USD {totalOutstandingUsd:N2}", $"ZiG {totalOutstandingZig:N2}", "Open exposure after collections are applied.");
        WriteExecutiveKpiCard(dashboard, row, 10, 12, ExecutiveCyan, "TRANSACTIONS", totalTransactions.ToString("N0"), $"Invoices {report.Summary.TotalInvoices:N0} | Payments {report.Summary.TotalPayments:N0}", "Combined invoice and incoming payment events.");
        WriteExecutiveKpiCard(dashboard, row, 13, 15, ExecutiveRose, "AVERAGE TRANSACTION", $"USD {averageTransactionUsd:N2}", $"ZiG {averageTransactionZig:N2}", "Average invoice value for the selected range.");
        row += 7;

        WriteExecutiveCallout(
            dashboard,
            row,
            15,
            "Executive Summary",
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
            "Trend Snapshot",
            "A quick scan of grouped sales, collections, and outstanding balances over the selected reporting periods.",
            ExecutiveCyan);
        row += 2;

        var trendHeaderRow = row;
        var trendHeaders = new[] { "Period", "Invoices", "Payments", "Sales USD", "Payments USD", "Outstanding USD", "Collection %", "Sales Bar", "Sales ZiG", "Payments ZiG", "Outstanding ZiG", "Collections Bar" };
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
                var collectionPercentUsd = CalculateExecutivePercent(period.IncomingPaymentsUsd, period.TotalSalesUsd);

                dashboard.Cell(row, 1).Value = period.Label;
                dashboard.Cell(row, 2).Value = period.InvoiceCount;
                dashboard.Cell(row, 3).Value = period.PaymentCount;
                dashboard.Cell(row, 4).Value = period.TotalSalesUsd;
                dashboard.Cell(row, 5).Value = period.IncomingPaymentsUsd;
                dashboard.Cell(row, 6).Value = outstandingUsd;
                SetExecutivePercentCell(dashboard.Cell(row, 7), collectionPercentUsd, highlight: true);
                dashboard.Cell(row, 8).Value = BuildExecutiveSignalBar(period.TotalSalesUsd, periodSalesUsdMax);
                dashboard.Cell(row, 9).Value = period.TotalSalesZig;
                dashboard.Cell(row, 10).Value = period.IncomingPaymentsZig;
                dashboard.Cell(row, 11).Value = outstandingZig;
                dashboard.Cell(row, 12).Value = BuildExecutiveSignalBar(period.IncomingPaymentsUsd, periodPaymentsUsdMax);

                dashboard.Range(row, 4, row, 6).Style.NumberFormat.Format = "#,##0.00";
                dashboard.Range(row, 9, row, 11).Style.NumberFormat.Format = "#,##0.00";
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
            "Customer Contribution",
            "Top accounts ranked by invoiced value, outstanding exposure, and collection quality.",
            ExecutiveRose);
        row += 2;

        var accountHeaderRow = row;
        var accountHeaders = new[] { "Card Code", "Card Name", "Sales USD", "Payments USD", "Outstanding USD", "Share USD %", "Sales ZiG", "Payments ZiG", "Outstanding ZiG", "Share ZiG %", "Contribution Bar", "Status" };
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
                SetExecutivePercentCell(dashboard.Cell(row, 6), usdShare, highlight: false);
                dashboard.Cell(row, 7).Value = account.TotalSalesZig;
                dashboard.Cell(row, 8).Value = account.IncomingPaymentsZig;
                dashboard.Cell(row, 9).Value = outstandingZig;
                SetExecutivePercentCell(dashboard.Cell(row, 10), zigShare, highlight: false);
                dashboard.Cell(row, 11).Value = BuildExecutiveSignalBar(pulseRatio, 1m);
                dashboard.Cell(row, 12).Value = ResolveExecutiveCollectionStatus(outstandingUsd, outstandingZig, account.CollectionRatePercentUsd, account.CollectionRatePercentZig);

                dashboard.Range(row, 3, row, 5).Style.NumberFormat.Format = "#,##0.00";
                dashboard.Range(row, 7, row, 9).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveOutstandingStyle(dashboard.Cell(row, 5), outstandingUsd);
                ApplyExecutiveOutstandingStyle(dashboard.Cell(row, 9), outstandingZig);
                ApplyExecutiveStatusBadge(dashboard.Cell(row, 12));
                row++;
            }

            StyleExecutiveTableRows(dashboard, dataStart, row - 1, accountHeaders.Length);
            ApplyExecutiveColumnFormatting(dashboard, dataStart, row - 1, wrapColumns: new[] { 2 }, centerColumns: new[] { 1, 11, 12 }, rightColumns: new[] { 3, 4, 5, 6, 7, 8, 9, 10 });
        }
        else
        {
            dashboard.Range(row, 1, row, accountHeaders.Length).Merge();
            dashboard.Cell(row, 1).Value = "No customer contribution rows were returned for this report.";
            dashboard.Cell(row, 1).Style.Font.Italic = true;
            dashboard.Cell(row, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            row++;
        }

        ApplyExecutiveColumnWidths(dashboard, 14, 26, 13, 13, 14, 12, 13, 13, 14, 12, 18, 13, 13, 13, 13);
        WriteExecutiveFooter(dashboard, row + 1, 15);
        FinalizeExecutiveSheet(dashboard, 15, freezeRow: 6, landscape: true);

        var visualsSheet = workbook.Worksheets.Add("Visuals");
        ConfigureExecutiveSheet(visualsSheet, 14, ExecutiveIndigo);
        var visualsRow = WriteExecutiveBannerSimple(
            visualsSheet,
            "Visuals",
            "Executive comparison panels aligned for management packs, review meetings, and finance narration.",
            report,
            14,
            ExecutiveIndigo);
        WriteExecutiveCallout(
            visualsSheet,
            visualsRow,
            14,
            "Visual Overview",
            "These visuals keep the workbook desktop-safe while giving finance and operations a clean comparison view of period performance and top-account exposure.");
        visualsRow += 5;

        const int visualsTrendFirstColumn = 1;
        const int visualsTrendLastColumn = 7;
        const int visualsAccountFirstColumn = 8;
        const int visualsAccountLastColumn = 14;
        var visualsFrameBottomRow = visualsRow + 16;

        WriteExecutiveChartContainer(
            visualsSheet,
            visualsRow,
            visualsTrendFirstColumn,
            visualsFrameBottomRow,
            visualsTrendLastColumn,
            "PERIOD SALES VS COLLECTIONS",
            "USD comparison panel sourced from the Trend Analysis sheet.",
            ExecutiveRoyalBlue);
        WriteExecutiveChartContainer(
            visualsSheet,
            visualsRow,
            visualsAccountFirstColumn,
            visualsFrameBottomRow,
            visualsAccountLastColumn,
            "TOP ACCOUNTS: SALES VS OUTSTANDING",
            "USD exposure panel sourced from the Customer Analysis sheet.",
            ExecutiveRose);

        // The frame body — everything below the frame's title and subtitle rows — is where
        // AddExecutiveChartsToAccountSalesWorkbook anchors the native charts once the
        // workbook has been saved. A chart floats over the cells it covers, so the summary
        // tables only render when there is nothing to plot and the frame would otherwise
        // be an empty box.
        var visualsChartTopRow = visualsRow + 2;

        if (!previewPeriods.Any())
        {
            WriteExecutiveVisualSummary(
                visualsSheet,
                visualsChartTopRow + 1,
                visualsTrendFirstColumn,
                visualsTrendLastColumn,
                new[] { "Period", "Sales USD", "Payments USD", "Outstanding USD" },
                Array.Empty<string[]>(),
                currencyColumns: new HashSet<int> { 2, 3, 4 },
                statusColumn: 0,
                accentColor: ExecutiveRoyalBlue);
        }

        if (!previewAccounts.Any())
        {
            WriteExecutiveVisualSummary(
                visualsSheet,
                visualsChartTopRow + 1,
                visualsAccountFirstColumn,
                visualsAccountLastColumn,
                new[] { "Card Code", "Sales USD", "Outstanding USD", "Status" },
                Array.Empty<string[]>(),
                currencyColumns: new HashSet<int> { 2, 3 },
                statusColumn: 4,
                accentColor: ExecutiveRose);
        }

        WriteExecutiveFooter(visualsSheet, visualsRow + 18, 14);
        ApplyExecutiveColumnWidths(visualsSheet, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12);
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

        var trendDetailHeaders = new[] { "Period", "Start (CAT)", "End (CAT)", "Accounts", "Invoices", "Payments", "Sales USD", "Payments USD", "Outstanding USD", "Collection USD %", "Sales Bar", "Sales ZiG", "Payments ZiG", "Outstanding ZiG" };
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
                var collectionPercentUsd = CalculateExecutivePercent(period.IncomingPaymentsUsd, period.TotalSalesUsd);

                trendSheet.Cell(trendRow, 1).Value = period.Label;
                trendSheet.Cell(trendRow, 2).Value = IAuditService.ToCAT(period.PeriodStartUtc);
                trendSheet.Cell(trendRow, 3).Value = IAuditService.ToCAT(period.PeriodEndUtc);
                trendSheet.Cell(trendRow, 4).Value = period.Accounts.Count;
                trendSheet.Cell(trendRow, 5).Value = period.InvoiceCount;
                trendSheet.Cell(trendRow, 6).Value = period.PaymentCount;
                trendSheet.Cell(trendRow, 7).Value = period.TotalSalesUsd;
                trendSheet.Cell(trendRow, 8).Value = period.IncomingPaymentsUsd;
                trendSheet.Cell(trendRow, 9).Value = outstandingUsd;
                SetExecutivePercentCell(trendSheet.Cell(trendRow, 10), collectionPercentUsd, highlight: true);
                trendSheet.Cell(trendRow, 11).Value = BuildExecutiveSignalBar(period.TotalSalesUsd, periodSalesUsdMax);
                trendSheet.Cell(trendRow, 12).Value = period.TotalSalesZig;
                trendSheet.Cell(trendRow, 13).Value = period.IncomingPaymentsZig;
                trendSheet.Cell(trendRow, 14).Value = outstandingZig;

                trendSheet.Range(trendRow, 2, trendRow, 3).Style.NumberFormat.Format = "dd mmm yyyy";
                trendSheet.Range(trendRow, 7, trendRow, 9).Style.NumberFormat.Format = "#,##0.00";
                trendSheet.Range(trendRow, 12, trendRow, 14).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveOutstandingStyle(trendSheet.Cell(trendRow, 9), outstandingUsd);
                ApplyExecutiveOutstandingStyle(trendSheet.Cell(trendRow, 14), outstandingZig);
                trendRow++;
            }

            StyleExecutiveTableRows(trendSheet, dataStart, trendRow - 1, trendDetailHeaders.Length);
            ApplyExecutiveTable(trendSheet, "TrendAnalysis", trendFreeze, trendRow - 1, trendDetailHeaders.Length);
            ApplyExecutiveColumnFormatting(trendSheet, dataStart, trendRow - 1, wrapColumns: Array.Empty<int>(), centerColumns: new[] { 1, 2, 3, 4, 5, 6, 10, 11 }, rightColumns: new[] { 7, 8, 9, 12, 13, 14 });
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

        // The Visuals frame is only seven columns wide, so the chart plots the same
        // last eight periods the dashboard previews rather than every reporting bucket.
        var trendChartDataStartRow = Math.Max(trendDataStartRow, trendDataEndRow - 7);

        ApplyExecutiveColumnWidths(trendSheet, 18, 15, 15, 11, 11, 11, 14, 14, 14, 12, 16, 14, 14, 14);
        WriteExecutiveFooter(trendSheet, trendRow + 1, 14);
        FinalizeExecutiveSheet(trendSheet, 14, freezeRow: trendFreeze, landscape: true);

        var accountSheet = workbook.Worksheets.Add("Customer Analysis");
        ConfigureExecutiveSheet(accountSheet, 14, ExecutiveCyan);
        var accountRow = WriteExecutiveBannerSimple(
            accountSheet,
            "Customer Analysis",
            "Contribution, outstanding balances, and settlement posture by requested account.",
            report,
            14,
            ExecutiveCyan);

        var accountDetailHeaders = new[] { "Card Code", "Card Name", "Invoices", "Payments", "Sales USD", "Collections USD", "Outstanding USD", "Share USD %", "Sales ZiG", "Collections ZiG", "Outstanding ZiG", "Share ZiG %", "Sales Bar", "Status" };
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
                SetExecutivePercentCell(accountSheet.Cell(accountRow, 8), usdShare, highlight: false);
                accountSheet.Cell(accountRow, 9).Value = account.TotalSalesZig;
                accountSheet.Cell(accountRow, 10).Value = account.IncomingPaymentsZig;
                accountSheet.Cell(accountRow, 11).Value = outstandingZig;
                SetExecutivePercentCell(accountSheet.Cell(accountRow, 12), zigShare, highlight: false);
                accountSheet.Cell(accountRow, 13).Value = BuildExecutiveSignalBar(account.TotalSalesUsd, accountSalesUsdMax);
                accountSheet.Cell(accountRow, 14).Value = ResolveExecutiveCollectionStatus(outstandingUsd, outstandingZig, account.CollectionRatePercentUsd, account.CollectionRatePercentZig);

                accountSheet.Range(accountRow, 5, accountRow, 7).Style.NumberFormat.Format = "#,##0.00";
                accountSheet.Range(accountRow, 9, accountRow, 11).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveOutstandingStyle(accountSheet.Cell(accountRow, 7), outstandingUsd);
                ApplyExecutiveOutstandingStyle(accountSheet.Cell(accountRow, 11), outstandingZig);
                ApplyExecutiveStatusBadge(accountSheet.Cell(accountRow, 14));
                accountRow++;
            }

            StyleExecutiveTableRows(accountSheet, dataStart, accountRow - 1, accountDetailHeaders.Length);
            ApplyExecutiveTable(accountSheet, "CustomerAnalysis", accountFreeze, accountRow - 1, accountDetailHeaders.Length);
            ApplyExecutiveColumnFormatting(accountSheet, dataStart, accountRow - 1, wrapColumns: new[] { 2 }, centerColumns: new[] { 1, 3, 4, 13, 14 }, rightColumns: new[] { 5, 6, 7, 8, 9, 10, 11, 12 });
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

        ApplyExecutiveColumnWidths(accountSheet, 12, 34, 10, 10, 14, 14, 14, 12, 14, 14, 14, 12, 16, 12);
        WriteExecutiveFooter(accountSheet, accountRow + 1, 14);
        FinalizeExecutiveSheet(accountSheet, 14, freezeRow: accountFreeze, landscape: true);

        var itemSheet = workbook.Worksheets.Add("Item Summary");
        ConfigureExecutiveSheet(itemSheet, 9, ExecutiveEmerald);
        var itemRow = WriteExecutiveBannerSimple(
            itemSheet,
            "Item Summary",
            "Item-level rollup suitable for audit tracing and sales mix review.",
            report,
            9,
            ExecutiveEmerald);

        var itemHeaders = new[] { "Card Code", "Card Name", "Item Code", "Item Name", "Invoices", "Qty Sold", "Sales USD", "Sales ZiG", "Value Bar" };
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
                itemSheet.Range(itemRow, 6, itemRow, 8).Style.NumberFormat.Format = "#,##0.00";
                itemRow++;
            }

            StyleExecutiveTableRows(itemSheet, dataStart, itemRow - 1, itemHeaders.Length);
            ApplyExecutiveTable(itemSheet, "ItemSummary", itemFreeze, itemRow - 1, itemHeaders.Length);
            ApplyExecutiveColumnFormatting(itemSheet, dataStart, itemRow - 1, wrapColumns: new[] { 2, 4 }, centerColumns: new[] { 1, 3, 5, 9 }, rightColumns: new[] { 6, 7, 8 });
        }
        else
        {
            itemSheet.Range(itemRow, 1, itemRow, itemHeaders.Length).Merge();
            itemSheet.Cell(itemRow, 1).Value = "No item summary rows are available for this report.";
            itemSheet.Cell(itemRow, 1).Style.Font.Italic = true;
            itemSheet.Cell(itemRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            itemRow++;
        }

        ApplyExecutiveColumnWidths(itemSheet, 12, 32, 14, 40, 10, 12, 14, 14, 14);
        WriteExecutiveFooter(itemSheet, itemRow + 1, 9);
        FinalizeExecutiveSheet(itemSheet, 9, freezeRow: itemFreeze, landscape: true);

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
                invoiceSheet.Cell(invoiceRow, 3).Value = IAuditService.ToCAT(invoice.DocumentDateUtc);
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
                    invoiceSheet.Range(invoiceRow, 1, invoiceRow, invoiceHeaders.Length).Style.Fill.BackgroundColor = ExecutiveSoftAmber;
                }

                invoiceSheet.Cell(invoiceRow, 3).Style.NumberFormat.Format = "dd mmm yyyy";
                invoiceSheet.Range(invoiceRow, 10, invoiceRow, 18).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveSourceBadge(invoiceSheet.Cell(invoiceRow, 2));
                ApplyExecutiveStatusBadge(invoiceSheet.Cell(invoiceRow, 8));
                ApplyExecutiveValueBandBadge(invoiceSheet.Cell(invoiceRow, 11));
                invoiceRow++;
            }

            StyleExecutiveTableRows(invoiceSheet, dataStart, invoiceRow - 1, invoiceHeaders.Length, preserveExistingFill: true);
            ApplyExecutiveTable(invoiceSheet, "InvoiceRegister", invoiceFreeze, invoiceRow - 1, invoiceHeaders.Length);
            ApplyExecutiveColumnFormatting(invoiceSheet, dataStart, invoiceRow - 1, wrapColumns: new[] { 5, 14 }, centerColumns: new[] { 2, 3, 8, 9, 11, 12 }, rightColumns: new[] { 10, 15, 16, 17, 18 });
        }
        else
        {
            invoiceSheet.Range(invoiceRow, 1, invoiceRow, invoiceHeaders.Length).Merge();
            invoiceSheet.Cell(invoiceRow, 1).Value = "No invoice line detail is available for this report.";
            invoiceSheet.Cell(invoiceRow, 1).Style.Font.Italic = true;
            invoiceSheet.Cell(invoiceRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            invoiceRow++;
        }

        ApplyExecutiveColumnWidths(invoiceSheet, 14, 10, 16, 12, 30, 14, 12, 12, 10, 14, 12, 8, 14, 38, 12, 14, 14, 14);
        WriteExecutiveFooter(invoiceSheet, invoiceRow + 1, 18);
        FinalizeExecutiveSheet(invoiceSheet, 18, freezeRow: invoiceFreeze, freezeCol: 4, landscape: true);

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
                paymentSheet.Cell(paymentRow, 3).Value = IAuditService.ToCAT(payment.PaymentDateUtc);
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
                    paymentSheet.Range(paymentRow, 1, paymentRow, paymentHeaders.Length).Style.Fill.BackgroundColor = ExecutiveSoftCyan;
                }

                paymentSheet.Cell(paymentRow, 3).Style.NumberFormat.Format = "dd mmm yyyy";
                paymentSheet.Range(paymentRow, 10, paymentRow, 12).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveSourceBadge(paymentSheet.Cell(paymentRow, 2));
                ApplyExecutiveStatusBadge(paymentSheet.Cell(paymentRow, 8));
                ApplyExecutiveValueBandBadge(paymentSheet.Cell(paymentRow, 15));
                paymentRow++;
            }

            StyleExecutiveTableRows(paymentSheet, dataStart, paymentRow - 1, paymentHeaders.Length, preserveExistingFill: true);
            ApplyExecutiveTable(paymentSheet, "PaymentRegister", paymentFreeze, paymentRow - 1, paymentHeaders.Length);
            ApplyExecutiveColumnFormatting(paymentSheet, dataStart, paymentRow - 1, wrapColumns: new[] { 5, 14 }, centerColumns: new[] { 2, 3, 8, 9, 13, 15 }, rightColumns: new[] { 10, 11, 12 });
        }
        else
        {
            paymentSheet.Range(paymentRow, 1, paymentRow, paymentHeaders.Length).Merge();
            paymentSheet.Cell(paymentRow, 1).Value = "No incoming payment detail is available for this report.";
            paymentSheet.Cell(paymentRow, 1).Style.Font.Italic = true;
            paymentSheet.Cell(paymentRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            paymentRow++;
        }

        ApplyExecutiveColumnWidths(paymentSheet, 14, 10, 16, 12, 30, 14, 12, 12, 10, 14, 14, 14, 12, 28, 12);
        WriteExecutiveFooter(paymentSheet, paymentRow + 1, 15);
        FinalizeExecutiveSheet(paymentSheet, 15, freezeRow: paymentFreeze, landscape: true);

        var applicationSheet = workbook.Worksheets.Add("Application Map");
        ConfigureExecutiveSheet(applicationSheet, 12, ExecutiveRoyalBlue);
        var applicationRow = WriteExecutiveBannerSimple(
            applicationSheet,
            "Application Map",
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
                applicationSheet.Cell(applicationRow, 3).Value = IAuditService.ToCAT(application.PaymentDateUtc);
                applicationSheet.Cell(applicationRow, 4).Value = application.CardCode;
                applicationSheet.Cell(applicationRow, 5).Value = application.CardName;
                applicationSheet.Cell(applicationRow, 6).Value = application.PaymentNumber;
                applicationSheet.Cell(applicationRow, 7).Value = application.PaymentEntry;
                applicationSheet.Cell(applicationRow, 8).Value = application.Status;
                applicationSheet.Cell(applicationRow, 9).Value = application.AppliedInvoiceReference;
                applicationSheet.Cell(applicationRow, 10).Value = application.InvoiceType;
                applicationSheet.Cell(applicationRow, 11).Value = application.Currency;
                applicationSheet.Cell(applicationRow, 12).Value = application.AppliedAmount;
                applicationSheet.Cell(applicationRow, 3).Style.NumberFormat.Format = "dd mmm yyyy";
                applicationSheet.Cell(applicationRow, 12).Style.NumberFormat.Format = "#,##0.00";
                ApplyExecutiveSourceBadge(applicationSheet.Cell(applicationRow, 2));
                ApplyExecutiveStatusBadge(applicationSheet.Cell(applicationRow, 8));
                applicationRow++;
            }

            StyleExecutiveTableRows(applicationSheet, dataStart, applicationRow - 1, applicationHeaders.Length, preserveExistingFill: true);
            ApplyExecutiveTable(applicationSheet, "ApplicationMap", applicationFreeze, applicationRow - 1, applicationHeaders.Length);
            ApplyExecutiveColumnFormatting(applicationSheet, dataStart, applicationRow - 1, wrapColumns: new[] { 5, 9 }, centerColumns: new[] { 2, 3, 8, 10, 11 }, rightColumns: new[] { 12 });
        }
        else
        {
            applicationSheet.Range(applicationRow, 1, applicationRow, applicationHeaders.Length).Merge();
            applicationSheet.Cell(applicationRow, 1).Value = "No payment application detail is available for this report.";
            applicationSheet.Cell(applicationRow, 1).Style.Font.Italic = true;
            applicationSheet.Cell(applicationRow, 1).Style.Font.FontColor = ExecutiveTextSecondary;
            applicationRow++;
        }

        ApplyExecutiveColumnWidths(applicationSheet, 14, 10, 16, 12, 30, 14, 12, 12, 18, 14, 10, 14);
        WriteExecutiveFooter(applicationSheet, applicationRow + 1, 12);
        FinalizeExecutiveSheet(applicationSheet, 12, freezeRow: applicationFreeze, landscape: true);

        return AddExecutiveChartsToAccountSalesWorkbook(
            WorkbookToBytes(workbook),
            new ExecutiveChartPlacement(
                HeaderRow: trendFreeze,
                DataStartRow: trendChartDataStartRow,
                DataEndRow: trendDataEndRow,
                FirstColumn: visualsTrendFirstColumn,
                LastColumn: visualsTrendLastColumn,
                TopRow: visualsChartTopRow,
                BottomRow: visualsFrameBottomRow),
            new ExecutiveChartPlacement(
                HeaderRow: accountFreeze,
                DataStartRow: accountDataStartRow,
                DataEndRow: accountChartDataEndRow,
                FirstColumn: visualsAccountFirstColumn,
                LastColumn: visualsAccountLastColumn,
                TopRow: visualsChartTopRow,
                BottomRow: visualsFrameBottomRow));
    }

    /// <summary>
    /// Where one Visuals chart sits on the sheet, and which source rows fill it.
    /// Rows and columns are one-based worksheet coordinates; the frame body spans
    /// <see cref="TopRow"/>..<see cref="BottomRow"/> and <see cref="FirstColumn"/>..<see cref="LastColumn"/>.
    /// </summary>
    private readonly record struct ExecutiveChartPlacement(
        int HeaderRow,
        int DataStartRow,
        int DataEndRow,
        int FirstColumn,
        int LastColumn,
        int TopRow,
        int BottomRow)
    {
        public bool HasData => DataEndRow >= DataStartRow;

        // Anchor markers are zero-based and sit on cell edges, so the from marker lands on
        // the frame's first cell and the to marker on the cell just past its last.
        public int AnchorFromColumn => FirstColumn - 1;
        public int AnchorFromRow => TopRow - 1;
        public int AnchorToColumn => LastColumn;
        public int AnchorToRow => BottomRow;
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
        string primaryValue,
        string? secondaryValue,
        string supportingText,
        decimal? primaryNumber = null,
        string? primaryNumberFormat = null)
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

        ws.Range(topRow + 2, startCol, topRow + 2, endCol).Merge();
        if (primaryNumber.HasValue)
        {
            // Numeric so Excel does not flag the card as a number stored as text.
            ws.Cell(topRow + 2, startCol).Value = primaryNumber.Value;
            ws.Cell(topRow + 2, startCol).Style.NumberFormat.Format = primaryNumberFormat ?? "#,##0";
        }
        else
        {
            ws.Cell(topRow + 2, startCol).Value = primaryValue;
        }
        ws.Cell(topRow + 2, startCol).Style.Font.Bold = true;
        ws.Cell(topRow + 2, startCol).Style.Font.FontSize = 17;
        ws.Cell(topRow + 2, startCol).Style.Font.FontColor = ExecutiveTextPrimary;
        ws.Cell(topRow + 2, startCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Cell(topRow + 2, startCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Range(topRow + 3, startCol, topRow + 3, endCol).Merge();
        ws.Cell(topRow + 3, startCol).Value = secondaryValue ?? string.Empty;
        ws.Cell(topRow + 3, startCol).Style.Font.Bold = !string.IsNullOrWhiteSpace(secondaryValue);
        ws.Cell(topRow + 3, startCol).Style.Font.FontSize = 10;
        ws.Cell(topRow + 3, startCol).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Cell(topRow + 3, startCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Cell(topRow + 3, startCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Range(topRow + 4, startCol, topRow + 4, endCol).Merge();
        ws.Cell(topRow + 4, startCol).Value = supportingText;
        ws.Cell(topRow + 4, startCol).Style.Font.FontSize = 9;
        ws.Cell(topRow + 4, startCol).Style.Font.FontColor = ExecutiveTextSecondary;
        ws.Cell(topRow + 4, startCol).Style.Alignment.WrapText = true;
        ws.Row(topRow + 2).Height = 20;
        ws.Row(topRow + 3).Height = 16;
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

    private static void WriteExecutiveVisualSummary(
        IXLWorksheet ws,
        int topRow,
        int leftCol,
        int rightCol,
        IReadOnlyList<string> headers,
        IReadOnlyList<string[]> rows,
        IReadOnlySet<int> currencyColumns,
        int statusColumn,
        XLColor accentColor)
    {
        if (headers.Count == 0 || rightCol < leftCol)
        {
            return;
        }

        var maxRows = 10;
        var headerRow = topRow;
        var dataStartRow = headerRow + 1;
        var displayedRows = rows.Take(maxRows).ToList();
        var columnCount = Math.Min(headers.Count, (rightCol - leftCol) + 1);

        for (var index = 0; index < columnCount; index++)
        {
            var headerCell = ws.Cell(headerRow, leftCol + index);
            headerCell.Value = headers[index];
            headerCell.Style.Font.Bold = true;
            headerCell.Style.Font.FontSize = 9;
            headerCell.Style.Font.FontColor = XLColor.White;
            headerCell.Style.Fill.BackgroundColor = accentColor;
            headerCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            headerCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            headerCell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            headerCell.Style.Border.OutsideBorderColor = ExecutiveBorder;
        }

        if (displayedRows.Count == 0)
        {
            ws.Range(dataStartRow, leftCol, dataStartRow, rightCol).Merge();
            ws.Cell(dataStartRow, leftCol).Value = "No visual comparison rows are available for this report.";
            ws.Cell(dataStartRow, leftCol).Style.Font.Italic = true;
            ws.Cell(dataStartRow, leftCol).Style.Font.FontColor = ExecutiveTextSecondary;
            ws.Cell(dataStartRow, leftCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            return;
        }

        for (var rowIndex = 0; rowIndex < displayedRows.Count; rowIndex++)
        {
            var currentRow = dataStartRow + rowIndex;
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var cell = ws.Cell(currentRow, leftCol + columnIndex);
                var value = columnIndex < displayedRows[rowIndex].Length ? displayedRows[rowIndex][columnIndex] : string.Empty;

                cell.Value = value;
                cell.Style.Font.FontSize = 9;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = ExecutiveBorder;
                cell.Style.Fill.BackgroundColor = rowIndex % 2 == 0 ? XLColor.White : ExecutiveCanvas;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                if (currencyColumns.Contains(columnIndex + 1) && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                {
                    cell.Value = amount;
                    cell.Style.NumberFormat.Format = "#,##0.00";
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                    if (headers[columnIndex].Contains("Outstanding", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyExecutiveOutstandingStyle(cell, amount);
                    }
                }
                else if (statusColumn == columnIndex + 1)
                {
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ApplyExecutiveStatusBadge(cell);
                }
                else
                {
                    cell.Style.Alignment.Horizontal = columnIndex == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Center;
                }
            }
        }
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

    private static void FinalizeExecutiveSheet(IXLWorksheet ws, int lastCol, int freezeRow = 0, int freezeCol = 0, bool landscape = false)
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

        if (freezeCol > 0)
        {
            ws.SheetView.FreezeColumns(freezeCol);
        }

        if (freezeRow > 0)
        {
            ws.PageSetup.SetRowsToRepeatAtTop(freezeRow, freezeRow);
        }

        ws.PageSetup.PageOrientation = landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetLeft(0.35);
        ws.PageSetup.Margins.SetRight(0.35);
        ws.PageSetup.Margins.SetTop(0.45);
        ws.PageSetup.Margins.SetBottom(0.45);
        ApplyPrintHeaderFooter(ws);
    }

    private static void WriteExecutiveFooter(IXLWorksheet ws, int row, int colSpan)
    {
        var generatedAt = CurrentCatNow();
        ws.Range(row, 1, row, colSpan).Merge();
        ws.Range(row, 1, row, colSpan).Style.Fill.BackgroundColor = ExecutiveCanvas;
        ws.Range(row, 1, row, colSpan).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        ws.Range(row, 1, row, colSpan).Style.Border.TopBorderColor = ExecutiveBorder;
        ws.Cell(row, 1).Value = $"CONFIDENTIAL  |  {CompanyName}  |  {SystemName}  |  Generated {generatedAt:dd MMM yyyy HH:mm} CAT";
        ws.Cell(row, 1).Style.Font.FontSize = 8;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = ExecutiveTextMuted;
        ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static string FormatExecutiveMoneyPair(decimal usd, decimal zig) =>
        $"USD {usd:N2}  |  ZiG {zig:N2}";

    private static string FormatExecutivePercent(decimal value) => $"{value:N2}%";

    private static decimal CalculateExecutivePercent(decimal numerator, decimal denominator) =>
        denominator <= 0 ? 0m : Math.Round((numerator / denominator) * 100m, 2);

    private static void SetExecutivePercentCell(IXLCell cell, decimal percentValue, bool highlight)
    {
        cell.Value = percentValue / 100m;
        cell.Style.NumberFormat.Format = "0.00%";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        if (highlight)
        {
            ApplyExecutiveCollectionPercentStyle(cell, percentValue);
        }
    }

    private static void ApplyExecutiveCollectionPercentStyle(IXLCell cell, decimal percentValue)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = ExecutiveBorder;

        if (percentValue >= 100m)
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftEmerald;
            cell.Style.Font.FontColor = ExecutiveEmerald;
        }
        else if (percentValue >= 70m)
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

    private static string BuildExecutiveSignalBar(decimal value, decimal maxValue, int width = 12)
    {
        if (maxValue <= 0)
        {
            return new string('░', width);
        }

        var ratio = Math.Max(0m, Math.Min(1m, value / maxValue));
        var filled = (int)Math.Round(ratio * width, MidpointRounding.AwayFromZero);
        return new string('█', filled) + new string('░', Math.Max(0, width - filled));
    }

    private static string ResolveExecutiveCollectionStatus(decimal outstandingUsd, decimal outstandingZig, decimal collectionUsd, decimal collectionZig)
    {
        if (outstandingUsd < 0 || outstandingZig < 0)
        {
            return "Credit";
        }

        if (outstandingUsd == 0 && outstandingZig == 0)
        {
            return "Settled";
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

        if (normalizedStatus.Contains("SETTLED") || normalizedStatus.Contains("PAID") || normalizedStatus.Contains("COMPLETED") || normalizedStatus.Contains("POSTED") || normalizedStatus.Contains("SYNCED"))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftEmerald;
            cell.Style.Font.FontColor = ExecutiveEmerald;
        }
        else if (normalizedStatus.Contains("CREDIT"))
        {
            cell.Style.Fill.BackgroundColor = ExecutiveSoftBlue;
            cell.Style.Font.FontColor = ExecutiveRoyalBlue;
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

    private static void ApplyExecutiveColumnWidths(IXLWorksheet ws, params double[] widths)
    {
        for (var index = 0; index < widths.Length; index++)
        {
            ws.Column(index + 1).Width = widths[index];
        }
    }

    private static void ApplyExecutiveTextWrap(IXLWorksheet ws, int firstRow, int lastRow, params int[] columns)
    {
        if (lastRow < firstRow)
        {
            return;
        }

        foreach (var column in columns)
        {
            ws.Range(firstRow, column, lastRow, column).Style.Alignment.WrapText = true;
        }
    }

    private static void ApplyExecutiveColumnFormatting(
        IXLWorksheet ws,
        int firstRow,
        int lastRow,
        IReadOnlyList<int> wrapColumns,
        IReadOnlyList<int> centerColumns,
        IReadOnlyList<int> rightColumns)
    {
        if (lastRow < firstRow)
        {
            return;
        }

        foreach (var column in wrapColumns)
        {
            ws.Range(firstRow, column, lastRow, column).Style.Alignment.WrapText = true;
        }

        foreach (var column in centerColumns)
        {
            ws.Range(firstRow, column, lastRow, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        foreach (var column in rightColumns)
        {
            ws.Range(firstRow, column, lastRow, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
    }

    private static void ApplyExecutiveTable(IXLWorksheet ws, string tableName, int headerRow, int lastRow, int lastCol)
    {
        if (lastRow <= headerRow)
        {
            return;
        }

        var table = ws.Range(headerRow, 1, lastRow, lastCol).CreateTable(SanitizeExecutiveTableName(tableName));
        table.Theme = XLTableTheme.None;
        table.ShowAutoFilter = true;
        table.ShowRowStripes = false;
    }

    private static string SanitizeExecutiveTableName(string tableName)
    {
        var buffer = new StringBuilder();
        foreach (var character in tableName)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                buffer.Append(character);
            }
        }

        if (buffer.Length == 0 || !char.IsLetter(buffer[0]))
        {
            buffer.Insert(0, 'T');
        }

        return buffer.ToString();
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

    /// <summary>
    /// Draws the two native charts that fill the frames on the Visuals sheet. ClosedXML
    /// cannot write charts, so the workbook is saved first and reopened with the OpenXML
    /// SDK to add them.
    /// </summary>
    private static byte[] AddExecutiveChartsToAccountSalesWorkbook(
        byte[] workbookBytes,
        ExecutiveChartPlacement trendPlacement,
        ExecutiveChartPlacement accountPlacement)
    {
        if (!trendPlacement.HasData && !accountPlacement.HasData)
        {
            return workbookBytes;
        }

        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            if (trendPlacement.HasData)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Visuals",
                    chartName: "Period Sales Collections",
                    sourceSheetName: "Trend Analysis",
                    headerRow: trendPlacement.HeaderRow,
                    categoryColumn: 1,
                    dataStartRow: trendPlacement.DataStartRow,
                    dataEndRow: trendPlacement.DataEndRow,
                    seriesColumns: new[] { 7, 8 },
                    seriesColors: new[] { "2563EB", "10B981" },
                    fromColumn: trendPlacement.AnchorFromColumn,
                    fromRow: trendPlacement.AnchorFromRow,
                    toColumn: trendPlacement.AnchorToColumn,
                    toRow: trendPlacement.AnchorToRow);
            }

            if (accountPlacement.HasData)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Visuals",
                    chartName: "Top Accounts Sales Outstanding",
                    sourceSheetName: "Customer Analysis",
                    headerRow: accountPlacement.HeaderRow,
                    categoryColumn: 1,
                    dataStartRow: accountPlacement.DataStartRow,
                    dataEndRow: accountPlacement.DataEndRow,
                    seriesColumns: new[] { 5, 7 },
                    seriesColors: new[] { "2563EB", "F43F5E" },
                    fromColumn: accountPlacement.AnchorFromColumn,
                    fromRow: accountPlacement.AnchorFromRow,
                    toColumn: accountPlacement.AnchorToColumn,
                    toRow: accountPlacement.AnchorToRow);
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

            // CT_BarSer order is idx, order, tx, spPr, invertIfNegative, ... — Excel rejects the file otherwise.
            series.Append(new C.ChartShapeProperties(
                new A.SolidFill(new A.RgbColorModelHex { Val = seriesColors[index] }),
                new A.Outline(new A.NoFill())));
            series.Append(new C.InvertIfNegative { Val = false });

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

        // Angled, smaller category labels: date and customer names are too long to sit flat.
        categoryAxis.Append(new C.TextProperties(
            new A.BodyProperties { Rotation = -2700000, Vertical = A.TextVerticalValues.Horizontal },
            new A.ListStyle(),
            new A.Paragraph(new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = 900 }))));

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

        // Without an explicit overlay flag Excel draws the legend on top of the category labels.
        chart.Append(new C.Legend(
            new C.LegendPosition { Val = C.LegendPositionValues.Bottom },
            new C.Layout(),
            new C.Overlay { Val = false },
            new C.TextProperties(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = 900 })))));
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

        var drawing = new DocumentFormat.OpenXml.Spreadsheet.Drawing { Id = worksheetPart.GetIdOfPart(drawingsPart) };

        // CT_Worksheet puts drawing ahead of these elements; appending past them makes Excel repair the file.
        // ClosedXML ends every sheet with a <tableParts count="0"/>, so this is always reached.
        var successor = worksheetPart.Worksheet.ChildElements
            .FirstOrDefault(element =>
                element is DocumentFormat.OpenXml.Spreadsheet.LegacyDrawing
                    or DocumentFormat.OpenXml.Spreadsheet.LegacyDrawingHeaderFooter
                    or DocumentFormat.OpenXml.Spreadsheet.Picture
                    or DocumentFormat.OpenXml.Spreadsheet.OleObjects
                    or DocumentFormat.OpenXml.Spreadsheet.Controls
                    or DocumentFormat.OpenXml.Spreadsheet.WebPublishItems
                    or DocumentFormat.OpenXml.Spreadsheet.TableParts
                    or DocumentFormat.OpenXml.Spreadsheet.WorksheetExtensionList);

        if (successor is null)
        {
            worksheetPart.Worksheet.Append(drawing);
        }
        else
        {
            worksheetPart.Worksheet.InsertBefore(drawing, successor);
        }

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
        using var workbook = NewWorkbook("Desktop Sales Report");
        var ws = AddSheet(workbook, "Desktop Sales");
        const int cols = 11;

        var row = WriteReportHeader(ws, "Desktop Sales Report", cols, fromDate, toDate);

        // KPI cards
        if (report != null)
        {
            WriteKpiCard(ws, row, 1, "Total Sales", report.TotalSalesCount, FormatCount);
            WriteKpiCard(ws, row, 2, "Total Amount", report.TotalSalesAmount, FormatMoney);
            WriteKpiCard(ws, row, 3, "Total VAT", report.TotalVatAmount, FormatMoney);
            WriteKpiCard(ws, row, 4, "Posted", report.PostedInvoiceCount, FormatCount, SuccessGreen);
            row += 3;
        }

        // Column headers. Date and Currency were both missing: the report is
        // date-ranged and the amounts are not all in one currency, so without them a
        // row could not be placed in time or read as an amount.
        var headers = new[] { "Date", "Reference", "Customer", "Card Code", "Warehouse", "Currency", "Amount", "VAT", "Paid", "Fiscal Status", "Consolidation" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        // Data rows
        var dataStart = row;
        foreach (var sale in sales.OrderBy(s => s.DocDate).ThenBy(s => s.ExternalReferenceId, StringComparer.OrdinalIgnoreCase))
        {
            ws.Cell(row, 1).Value = sale.DocDate;
            ws.Cell(row, 1).Style.NumberFormat.Format = FormatDate;
            ws.Cell(row, 2).Value = sale.ExternalReferenceId;
            ws.Cell(row, 3).Value = sale.CardName ?? sale.CardCode;
            ws.Cell(row, 4).Value = sale.CardCode;
            ws.Cell(row, 5).Value = sale.WarehouseCode;
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 6).Value = sale.Currency;
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 7).Value = sale.TotalAmount;
            ws.Cell(row, 8).Value = sale.VatAmount;
            ws.Cell(row, 9).Value = sale.AmountPaid;
            ws.Range(row, 7, row, 9).Style.NumberFormat.Format = FormatMoney;
            ws.Cell(row, 10).Value = sale.FiscalizationStatus;
            ws.Cell(row, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 11).Value = sale.ConsolidationStatus;
            ws.Cell(row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, "No desktop sales fell in this period.");

        // One totals row per currency. Desktop sales arrive in both USD and ZWG, so a
        // single sum down the Amount column would add two currencies together.
        row = WriteCurrencyTotals(
            ws,
            row,
            cols,
            currencyColumn: 6,
            labelColumn: 5,
            sales.GroupBy(sale => string.IsNullOrWhiteSpace(sale.Currency) ? "-" : sale.Currency.Trim()),
            (sheet, totalRow, group) =>
            {
                sheet.Cell(totalRow, 7).Value = group.Sum(sale => sale.TotalAmount);
                sheet.Cell(totalRow, 8).Value = group.Sum(sale => sale.VatAmount);
                sheet.Cell(totalRow, 9).Value = group.Sum(sale => sale.AmountPaid);
                sheet.Range(totalRow, 7, totalRow, 9).Style.NumberFormat.Format = FormatMoney;
            });

        WriteFooter(ws, row - 1, cols);
        FinalizeSheet(ws, cols, headerRow, landscape: true);
        return WorkbookToBytes(workbook);
    }

    // ─── Local Stock Export ──────────────────────────────────────

    public byte[] ExportLocalStockToExcel(LocalStockResultDto stock)
    {
        using var workbook = NewWorkbook("Local Stock Snapshot");
        var ws = AddSheet(workbook, "Local Stock");
        const int cols = 8;

        var row = WriteReportHeader(ws, "Local Stock Snapshot", cols,
            subtitle: $"Warehouse: {stock.WarehouseCode}  |  Date: {stock.SnapshotDate:dd MMM yyyy}  |  Status: {stock.SnapshotStatus}");

        // KPI cards
        var inStock = stock.Items.Count(i => i.AvailableQuantity > 0);
        var outOfStock = stock.Items.Count(i => i.AvailableQuantity <= 0);
        var adjusted = stock.Items.Count(i => i.TransferAdjustment != 0);
        WriteKpiCard(ws, row, 1, "Total Items", stock.Items.Count, FormatCount);
        WriteKpiCard(ws, row, 2, "In Stock", inStock, FormatCount, SuccessGreen);
        WriteKpiCard(ws, row, 3, "Out of Stock", outOfStock, FormatCount, outOfStock > 0 ? DangerRed : SuccessGreen);
        WriteKpiCard(ws, row, 4, "Transfer Adjusted", adjusted, FormatCount);
        row += 3;

        // Column headers. The batch rows used to be interleaved into the item rows,
        // which made the sheet unsortable and unfilterable and left the batch
        // quantities sitting in the item quantity columns where a SUM would
        // double-count them. Batches now get their own row type, flagged in column 1.
        var headers = new[] { "Row", "Item Code", "Description", "Available Qty", "Original Qty", "Adjustment", "Batches", "Warehouse" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        // Data rows
        var dataStart = row;
        foreach (var item in stock.Items)
        {
            ws.Cell(row, 1).Value = "Item";
            ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 2).Value = item.ItemCode;
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 3).Value = item.ItemDescription ?? "";
            ws.Cell(row, 4).Value = item.AvailableQuantity;
            ws.Cell(row, 4).Style.NumberFormat.Format = FormatQuantity;
            if (item.AvailableQuantity <= 0)
                ws.Cell(row, 4).Style.Font.FontColor = DangerRed;
            ws.Cell(row, 5).Value = item.OriginalQuantity;
            ws.Cell(row, 5).Style.NumberFormat.Format = FormatQuantity;
            ws.Cell(row, 6).Value = item.TransferAdjustment;
            ws.Cell(row, 6).Style.NumberFormat.Format = "+#,##0.00;[Red]-#,##0.00;0.00";
            if (item.TransferAdjustment > 0) ws.Cell(row, 6).Style.Font.FontColor = SuccessGreen;
            ws.Cell(row, 7).Value = item.Batches.Count;
            ws.Cell(row, 7).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 8).Value = item.WarehouseCode;
            ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;

            // Batch detail rows
            if (item.Batches.Count > 1)
            {
                foreach (var batch in item.Batches.OrderBy(b => b.ExpiryDate))
                {
                    ws.Cell(row, 1).Value = "Batch";
                    ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(row, 2).Value = item.ItemCode;
                    ws.Cell(row, 3).Value = $"Batch {batch.BatchNumber ?? "N/A"}" +
                        (batch.ExpiryDate.HasValue ? $" — expires {batch.ExpiryDate.Value:dd MMM yyyy}" : "");
                    ws.Cell(row, 3).Style.Alignment.Indent = 1;
                    ws.Cell(row, 4).Value = batch.AvailableQuantity;
                    ws.Cell(row, 4).Style.NumberFormat.Format = FormatQuantity;
                    ws.Cell(row, 5).Value = batch.OriginalQuantity;
                    ws.Cell(row, 5).Style.NumberFormat.Format = FormatQuantity;
                    ws.Cell(row, 8).Value = item.WarehouseCode;
                    ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Range(row, 1, row, cols).Style.Font.FontSize = 9;
                    ws.Range(row, 1, row, cols).Style.Font.FontColor = MutedText;
                    row++;
                }
            }
        }

        var lastData = row - 1;
        row = FinishTable(ws, headerRow, dataStart, row, cols, "This warehouse snapshot returned no stock.");

        // Filter the Row column to "Item" before reading these: unfiltered they count
        // each batched item twice, which is exactly why SUBTOTAL is used here.
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 3).Value = "Filter Row = Item for an item-level total";
        ws.Cell(row, 3).Style.Font.Italic = true;
        ws.Cell(row, 3).Style.Font.FontSize = 8;
        WriteSubtotal(ws, row, 4, dataStart, lastData, FormatQuantity);
        WriteSubtotal(ws, row, 5, dataStart, lastData, FormatQuantity);
        WriteSubtotal(ws, row, 7, dataStart, lastData, FormatCount);
        ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleTotalsRow(ws, row, cols);

        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, headerRow, landscape: true);
        return WorkbookToBytes(workbook);
    }

    /// <summary>
    /// The Mobile Orders review queue as a workbook: the columns the page's
    /// table shows, plus the submission detail — device, sync state, capture
    /// coordinates — that the page keeps in the drawer rather than the row.
    /// </summary>
    public byte[] ExportMobileOrdersToExcel(IReadOnlyCollection<SalesOrderDto> orders, string title)
    {
        using var workbook = NewWorkbook(title);
        var ws = AddSheet(workbook, title);
        const int cols = 14;

        var row = WriteReportHeader(ws, title, cols, subtitle: $"Orders listed: {orders.Count:N0}");

        WriteKpiCard(ws, row, 1, "Orders", orders.Count, FormatCount);
        WriteKpiCard(ws, row, 2, "Draft", orders.Count(order => order.Status == SalesOrderStatus.Draft), FormatCount);
        WriteKpiCard(ws, row, 3, "Pending", orders.Count(order => order.Status == SalesOrderStatus.Pending), FormatCount, WarningOrange);
        WriteKpiCard(ws, row, 4, "Approved", orders.Count(order => order.Status == SalesOrderStatus.Approved), FormatCount, SuccessGreen);
        WriteKpiCard(ws, row, 5, "Not In SAP", orders.Count(order => !order.IsSynced), FormatCount);
        row += 3;

        var headers = new[]
        {
            "Order #", "Customer", "Customer Code", "Lines", "Device", "Sync",
            "Ordered", "Received (CAT)", "Delivery", "Status", "Currency", "Total", "SAP Doc #", "Captured At"
        };

        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        var dataStart = row;
        foreach (var order in orders)
        {
            ws.Cell(row, 1).Value = order.OrderNumber;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = order.CardName ?? string.Empty;
            ws.Cell(row, 3).Value = order.CardCode ?? string.Empty;
            ws.Cell(row, 4).Value = order.Lines?.Count ?? 0;
            ws.Cell(row, 4).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = string.IsNullOrWhiteSpace(order.DeviceInfo) ? "Not captured" : order.DeviceInfo.Trim();
            ws.Cell(row, 6).Value = order.IsSynced ? "Synced" : "Queued";
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (!order.IsSynced)
                ws.Cell(row, 6).Style.Font.FontColor = WarningOrange;

            ws.Cell(row, 7).Value = order.OrderDate;
            ws.Cell(row, 7).Style.NumberFormat.Format = FormatDate;
            // The capture timestamp is a real instant in CAT, not its rendering, so the
            // column can be sorted and a submission window read off it.
            ws.Cell(row, 8).Value = IAuditService.ToCAT(EnsureUtc(order.CreatedAt));
            ws.Cell(row, 8).Style.NumberFormat.Format = FormatTimestamp;

            // Left blank rather than filled with a dash: a placeholder in a date column
            // makes the whole column text and stops it sorting.
            if (order.DeliveryDate.HasValue)
            {
                ws.Cell(row, 9).Value = order.DeliveryDate.Value;
                ws.Cell(row, 9).Style.NumberFormat.Format = FormatDate;
            }

            ws.Cell(row, 10).Value = order.Status.ToString();
            ws.Cell(row, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 10).Style.Font.FontColor = order.Status switch
            {
                SalesOrderStatus.Approved or SalesOrderStatus.Fulfilled or SalesOrderStatus.Invoiced => SuccessGreen,
                SalesOrderStatus.Pending or SalesOrderStatus.PartiallyFulfilled => WarningOrange,
                SalesOrderStatus.Cancelled or SalesOrderStatus.Rejected => DangerRed,
                _ => MutedText
            };

            ws.Cell(row, 11).Value = order.Currency ?? string.Empty;
            ws.Cell(row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 12).Value = order.DocTotal;
            ws.Cell(row, 12).Style.NumberFormat.Format = FormatMoney;
            ws.Cell(row, 13).Value = order.SAPDocNum.HasValue
                ? order.SAPDocNum.Value.ToString(CultureInfo.InvariantCulture)
                : order.Status == SalesOrderStatus.Approved ? "Pending" : "-";
            ws.Cell(row, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 14).Value = order.Latitude.HasValue && order.Longitude.HasValue
                ? $"{order.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture)}, {order.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture)}"
                : "-";
            row++;
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, "No mobile orders matched this queue's filters.");

        // One totals row per currency. Mobile orders come in USD and ZWG, so a
        // single sum down the Total column would add two currencies together.
        row = WriteCurrencyTotals(
            ws,
            row,
            cols,
            currencyColumn: 11,
            labelColumn: 10,
            orders.GroupBy(order => string.IsNullOrWhiteSpace(order.Currency) ? "-" : order.Currency!.Trim()),
            (sheet, totalRow, group) =>
            {
                sheet.Cell(totalRow, 12).Value = group.Sum(order => order.DocTotal);
                sheet.Cell(totalRow, 12).Style.NumberFormat.Format = FormatMoney;
            });

        WriteFooter(ws, row - 1, cols);
        FinalizeSheet(ws, cols, headerRow, landscape: true);
        return WorkbookToBytes(workbook);
    }

    /// <summary>
    /// One route customer's trading: the sales, what they buy, and anything still on order.
    ///
    /// Three sheets rather than three stacked blocks, because each has its own column shape and the
    /// sales sheet is the one people filter and sort.
    /// </summary>
    public byte[] ExportRouteCustomerSalesToExcel(RouteCustomerSalesDetailModel detail, string routeLabel)
    {
        var title = $"Route customer sales — {detail.Customer.Name}";
        using var workbook = NewWorkbook(title);

        WriteRouteCustomerSalesSheet(workbook, detail, routeLabel, title);
        WriteRouteCustomerProductMixSheet(workbook, detail.ProductMix, detail.From, detail.To,
            $"Product mix — {detail.Customer.Name}");

        if (detail.Orders.Count > 0)
        {
            WriteRouteCustomerOrdersSheet(workbook, detail.Orders);
        }

        return WorkbookToBytes(workbook);
    }

    private void WriteRouteCustomerSalesSheet(
        XLWorkbook workbook,
        RouteCustomerSalesDetailModel detail,
        string routeLabel,
        string title)
    {
        var ws = AddSheet(workbook, "Sales");
        const int cols = 11;

        var row = WriteReportHeader(ws, title, cols, detail.From, detail.To,
            $"{detail.Customer.Code} on {routeLabel}");

        WriteKpiCard(ws, row, 1, "Sales", detail.SaleCount, FormatCount);
        WriteKpiCard(ws, row, 2, "Lines", detail.LineCount, FormatCount);
        WriteKpiCard(ws, row, 3, "Last sale",
            detail.LastSaleAt?.ToString(FormatDate, CultureInfo.InvariantCulture) ?? "Never");
        WriteKpiCard(ws, row, 4, "Days since",
            detail.DaysSinceLastSale?.ToString("N0", CultureInfo.InvariantCulture) ?? "—",
            detail.DaysSinceLastSale is > 30 ? WarningOrange : null);
        WriteKpiCard(ws, row, 5, "Value", DescribeCurrencyTotals(detail.TotalsByCurrency));
        row += 3;

        var headers = new[]
        {
            "Sold", "Reference", "Source", "Currency", "Total", "VAT", "Paid",
            "Payment", "Status", "SAP Doc #", "ZIMRA receipt"
        };

        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        var dataStart = row;
        foreach (var sale in detail.Sales)
        {
            ws.Cell(row, 1).Value = sale.SoldAt;
            ws.Cell(row, 1).Style.NumberFormat.Format = FormatDate;
            ws.Cell(row, 2).Value = sale.Reference;
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 3).Value = DescribeSaleSource(sale.Source);
            ws.Cell(row, 4).Value = sale.Currency;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = sale.Total;
            ws.Cell(row, 5).Style.NumberFormat.Format = FormatMoney;

            // An online sale carries no tax split — that lives on the SAP invoice — so the cell is left
            // empty rather than filled with a zero that reads as "no VAT was charged".
            if (sale.Source == RouteCustomerSaleSource.OfflineVanSale)
            {
                ws.Cell(row, 6).Value = sale.VatAmount;
                ws.Cell(row, 6).Style.NumberFormat.Format = FormatMoney;
            }

            ws.Cell(row, 7).Value = sale.AmountPaid;
            ws.Cell(row, 7).Style.NumberFormat.Format = FormatMoney;
            ws.Cell(row, 8).Value = sale.PaymentMethod ?? "-";
            ws.Cell(row, 9).Value = sale.Status;
            ws.Cell(row, 10).Value = sale.SapDocNum.HasValue
                ? sale.SapDocNum.Value.ToString(CultureInfo.InvariantCulture)
                : "Pending";
            ws.Cell(row, 10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (!sale.SapDocNum.HasValue)
            {
                ws.Cell(row, 10).Style.Font.FontColor = WarningOrange;
            }

            ws.Cell(row, 11).Value = sale.ReceiptGlobalNo.HasValue
                ? sale.ReceiptGlobalNo.Value.ToString(CultureInfo.InvariantCulture)
                : "-";
            ws.Cell(row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols,
            "This customer made no purchases inside the selected dates.");

        row = WriteCurrencyTotals(
            ws,
            row,
            cols,
            currencyColumn: 4,
            labelColumn: 3,
            detail.Sales.GroupBy(sale => sale.Currency),
            (sheet, totalRow, group) =>
            {
                sheet.Cell(totalRow, 5).Value = group.Sum(sale => sale.Total);
                sheet.Cell(totalRow, 5).Style.NumberFormat.Format = FormatMoney;
                sheet.Cell(totalRow, 7).Value = group.Sum(sale => sale.AmountPaid);
                sheet.Cell(totalRow, 7).Style.NumberFormat.Format = FormatMoney;
            });

        WriteFooter(ws, row - 1, cols);
        FinalizeSheet(ws, cols, headerRow, landscape: true);
    }

    private void WriteRouteCustomerProductMixSheet(
        XLWorkbook workbook,
        List<RouteCustomerProductMixRowModel> items,
        DateTime from,
        DateTime to,
        string title)
    {
        var ws = AddSheet(workbook, "Product mix");
        const int cols = 7;

        var row = WriteReportHeader(ws, title, cols, from, to, $"Items bought: {items.Count:N0}");

        var headers = new[] { "Item", "Description", "Quantity", "UoM", "Times bought", "Customers", "Value" };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        var dataStart = row;
        foreach (var item in items)
        {
            ws.Cell(row, 1).Value = item.ItemCode;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = item.ItemDescription ?? string.Empty;
            ws.Cell(row, 3).Value = item.Quantity;
            ws.Cell(row, 3).Style.NumberFormat.Format = FormatQuantity;
            ws.Cell(row, 4).Value = item.UoMCode ?? "-";
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = item.LineCount;
            ws.Cell(row, 5).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 6).Value = item.CustomerCount;
            ws.Cell(row, 6).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Text, not a number: the value can be in two currencies, and one cell holding "USD 40.00 /
            // ZWG 900.00" is honest where a single numeric total would not be.
            ws.Cell(row, 7).Value = DescribeCurrencyTotals(item.TotalsByCurrency);
            row++;
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, "No items were sold inside the selected dates.");
        WriteFooter(ws, row - 1, cols);
        FinalizeSheet(ws, cols, headerRow);
    }

    private void WriteRouteCustomerOrdersSheet(XLWorkbook workbook, List<RouteCustomerOrderModel> orders)
    {
        var ws = AddSheet(workbook, "Orders");
        const int cols = 6;

        var row = WriteReportHeader(ws, "Orders", cols,
            subtitle: "Orders are shown for context and are not counted in the sales totals — a mobile order is priced after capture.");

        var headers = new[] { "Order #", "Ordered", "Status", "Lines", "Currency", "Value" };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        var dataStart = row;
        foreach (var order in orders)
        {
            ws.Cell(row, 1).Value = order.OrderNumber;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = order.OrderDate;
            ws.Cell(row, 2).Style.NumberFormat.Format = FormatDate;
            ws.Cell(row, 3).Value = order.IsInvoiced ? "Invoiced" : order.Status;
            ws.Cell(row, 4).Value = order.LineCount;
            ws.Cell(row, 4).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = order.Currency ?? "-";
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            if (order.IsAwaitingPricing)
            {
                ws.Cell(row, 6).Value = "Awaiting pricing";
                ws.Cell(row, 6).Style.Font.FontColor = MutedText;
                ws.Cell(row, 6).Style.Font.Italic = true;
            }
            else
            {
                ws.Cell(row, 6).Value = order.DocTotal;
                ws.Cell(row, 6).Style.NumberFormat.Format = FormatMoney;
            }

            row++;
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, "This customer has no orders inside the selected dates.");
        WriteFooter(ws, row - 1, cols);
        FinalizeSheet(ws, cols, headerRow);
    }

    /// <summary>
    /// Every route customer against what they bought, one row each, with the route repeated down a
    /// column so the sheet can be filtered and pivoted rather than read only in the order it was written.
    /// </summary>
    public byte[] ExportRouteSalesSummaryToExcel(
        RouteCustomerSalesSummaryModel summary,
        IReadOnlyDictionary<string, string> routeLabels)
    {
        const string title = "Route customer sales summary";
        using var workbook = NewWorkbook(title);
        var ws = AddSheet(workbook, "Summary");
        const int cols = 11;

        var rows = summary.Routes.SelectMany(route => route.Customers).ToList();

        var row = WriteReportHeader(ws, title, cols, summary.From, summary.To,
            $"{summary.Routes.Count:N0} route(s), {rows.Count:N0} customer(s). " +
            $"Sales, lines and value are for the dates above; the first and last sale are all time. " +
            $"Dormant means bought before but not for more than {summary.DormantDays:N0} days; " +
            $"never bought means no purchase on any date.");

        WriteKpiCard(ws, row, 1, "Customers", rows.Count, FormatCount);
        WriteKpiCard(ws, row, 2, "Bought", rows.Count(customer => customer.SaleCount > 0), FormatCount, SuccessGreen);
        WriteKpiCard(ws, row, 3, "Dormant",
            rows.Count(customer => customer.DaysSinceLastSale is { } days && days > summary.DormantDays),
            FormatCount, WarningOrange);
        // The all-time date, not the window's sale count: a shop whose last purchase predates the dates
        // has an empty window and has still bought, and counting it here called it a new outlet.
        WriteKpiCard(ws, row, 4, "Never bought", rows.Count(customer => customer.LastSaleAt is null),
            FormatCount, DangerRed);
        WriteKpiCard(ws, row, 5, "Value",
            DescribeCurrencyTotals(summary.Routes.SelectMany(route => route.TotalsByCurrency).ToList()));
        row += 3;

        var headers = new[]
        {
            "Route", "Code", "Customer", "Phone", "Status", "Sales", "Lines",
            "Value", "First sale (all time)", "Last sale (all time)", "Days since"
        };

        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        var dataStart = row;
        foreach (var customer in rows)
        {
            ws.Cell(row, 1).Value = routeLabels.TryGetValue(customer.AssignedBusinessPartnerCode, out var label)
                ? label
                : customer.AssignedBusinessPartnerCode;
            ws.Cell(row, 2).Value = customer.Code;
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 3).Value = customer.Name;
            ws.Cell(row, 4).Value = customer.Phone ?? "-";

            ws.Cell(row, 5).Value = DescribeCustomerStanding(customer, summary.DormantDays);
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Style.Font.FontColor = customer.LastSaleAt is null
                ? DangerRed
                : customer.DaysSinceLastSale > summary.DormantDays
                    ? WarningOrange
                    : SuccessGreen;

            ws.Cell(row, 6).Value = customer.SaleCount;
            ws.Cell(row, 6).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 7).Value = customer.LineCount;
            ws.Cell(row, 7).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 8).Value = DescribeCurrencyTotals(customer.TotalsByCurrency);

            // Blank rather than a dash: a placeholder turns the whole column to text and stops it sorting,
            // which is the one thing a "who has not bought lately" report is read by.
            if (customer.FirstSaleAt.HasValue)
            {
                ws.Cell(row, 9).Value = customer.FirstSaleAt.Value;
                ws.Cell(row, 9).Style.NumberFormat.Format = FormatDate;
            }

            if (customer.LastSaleAt.HasValue)
            {
                ws.Cell(row, 10).Value = customer.LastSaleAt.Value;
                ws.Cell(row, 10).Style.NumberFormat.Format = FormatDate;
            }

            if (customer.DaysSinceLastSale.HasValue)
            {
                ws.Cell(row, 11).Value = customer.DaysSinceLastSale.Value;
                ws.Cell(row, 11).Style.NumberFormat.Format = FormatCount;
                ws.Cell(row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            row++;
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, "No route customers matched this report's filters.");
        WriteFooter(ws, row - 1, cols);
        FinalizeSheet(ws, cols, headerRow, landscape: true);
        return WorkbookToBytes(workbook);
    }

    private static string DescribeSaleSource(RouteCustomerSaleSource source) => source switch
    {
        RouteCustomerSaleSource.OfflineVanSale => "Van sale",
        RouteCustomerSaleSource.OnlineInvoice => "Online invoice",
        _ => "Sale"
    };

    /// <summary>
    /// Which of the three standings a customer is in, decided on the all-time last sale.
    ///
    /// "Never bought" is a claim about the shop's whole history, so only a missing last sale can make it.
    /// Reading the window's sale count instead — which this did — labelled every shop whose last purchase
    /// fell before the report dates as one that had never been converted, and the two need opposite visits.
    /// </summary>
    private static string DescribeCustomerStanding(RouteCustomerSalesRowModel customer, int dormantDays)
    {
        if (customer.LastSaleAt is null)
        {
            return "Never bought";
        }

        return customer.DaysSinceLastSale > dormantDays ? "Dormant" : "Active";
    }

    /// <summary>
    /// Per-currency totals as one readable string — "USD 40.00 / ZWG 900.00".
    ///
    /// A cell, a KPI card and a footer all need this, and all of them have room for one value. Adding
    /// the currencies together to fit would produce a number that is not any amount of money.
    /// </summary>
    private static string DescribeCurrencyTotals(IReadOnlyCollection<RouteCustomerSalesTotalsModel> totals)
    {
        if (totals.Count == 0)
        {
            return "—";
        }

        return string.Join(" / ", totals
            .GroupBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Currency = group.Key, Gross = group.Sum(total => total.Gross) })
            .OrderByDescending(total => total.Gross)
            .Select(total => $"{total.Currency} {total.Gross.ToString("N2", CultureInfo.InvariantCulture)}"));
    }

    // ═══════════════════════════════════════════════════════════════
    // G/L ACCOUNT LEDGER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// One G/L account's journal lines over a period, with the balance they carry.
    /// </summary>
    /// <remarks>
    /// The screen pages this table fifty rows at a time; the workbook carries every line the API
    /// returned, which is most of the reason to export it. What it must not lose on the way out is
    /// the reconciliation against SAP — on the page that is a banner nobody can miss, and a file of
    /// figures that does not repeat it reads as a file of figures that agree.
    /// </remarks>
    public byte[] ExportGLAccountLedgerToExcel(GLAccountLedgerResponse ledger)
    {
        var title = string.IsNullOrWhiteSpace(ledger.AccountName)
            ? $"G/L ledger — {ledger.AccountCode}"
            : $"G/L ledger — {ledger.AccountCode} {ledger.AccountName}";

        using var workbook = NewWorkbook(title);
        var ws = AddSheet(workbook, "Ledger");
        const int cols = 12;

        var money = MoneyFormatFor(ledger.Currency);

        var row = WriteReportHeader(ws, title, cols, ledger.FromDate, ledger.ToDate);

        WriteKpiCard(ws, row, 1, "Opening balance", ledger.OpeningBalance, money);
        WriteKpiCard(ws, row, 2, "Debits", ledger.TotalDebits, money);
        WriteKpiCard(ws, row, 3, "Credits", ledger.TotalCredits, money);
        WriteKpiCard(ws, row, 4, "Closing balance", ledger.ClosingBalance, money);
        WriteKpiCard(ws, row, 5, "SAP balance", ledger.SapBalance, money, ReconciliationColour(ledger));
        row += 3;

        // Stated whichever way it came out. "Agrees with SAP" is worth the line: an export with no
        // verdict on it cannot be told apart from one whose verdict was bad.
        row = WriteNotice(ws, row, cols, ReconciliationNotice(ledger), ReconciliationColour(ledger));

        if (ledger.IsTruncated)
        {
            row = WriteNotice(ws, row, cols,
                $"This period holds more than {ledger.LineLimit:N0} lines and only the first "
                + $"{ledger.LineLimit:N0} were read, so the closing balance below stops short of "
                + $"{ledger.ToDate:dd MMM yyyy}. Export a narrower date range to see the rest.",
                WarningOrange);
        }

        row++;

        var headers = new[]
        {
            "Date", "Document", "Journal", "Type", "Document type", "Partner",
            "Details", "Posted by", "Offset", "Debit", "Credit", "Balance"
        };

        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        // The brought-forward line. Balance is a running total, so without the figure it starts
        // from there is no way to check a single number in that column against the sheet itself.
        var openingRow = row;
        ws.Cell(row, 7).Value = $"Opening balance brought forward from before {ledger.FromDate:dd MMM yyyy}";
        ws.Cell(row, 12).Value = ledger.OpeningBalance;
        ws.Cell(row, 12).Style.NumberFormat.Format = money;
        row++;

        var firstLineRow = row;
        foreach (var line in ledger.Lines)
        {
            ws.Cell(row, 1).Value = line.Date;
            ws.Cell(row, 1).Style.NumberFormat.Format = FormatDate;
            WriteIdentifier(ws.Cell(row, 2), line.DocumentNumber);
            ws.Cell(row, 2).Style.Font.Bold = true;

            // Already an int on the way in, so it needs no parsing — but the same format, for the
            // same reason as the column beside it.
            ws.Cell(row, 3).Value = line.TransactionNumber;
            ws.Cell(row, 3).Style.NumberFormat.Format = "0";
            ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            ws.Cell(row, 4).Value = line.OriginCode;
            ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 5).Value = line.DocumentType;
            ws.Cell(row, 6).Value = line.PartnerCode ?? "-";
            ws.Cell(row, 7).Value = line.Description ?? string.Empty;
            WriteIdentifier(ws.Cell(row, 8), line.CreatedBy);
            ws.Cell(row, 9).Value = line.OffsetAccount ?? "-";
            ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // The other side is left empty rather than zeroed, as it is on screen. A column of
            // 0.00s beside the real figures is the thing that makes a ledger unreadable, and a
            // zero in the debit column of a credit line is not a posting of nothing.
            if (line.Debit != 0)
            {
                ws.Cell(row, 10).Value = line.Debit;
                ws.Cell(row, 10).Style.NumberFormat.Format = money;
            }

            if (line.Credit != 0)
            {
                ws.Cell(row, 11).Value = line.Credit;
                ws.Cell(row, 11).Style.NumberFormat.Format = money;
            }

            ws.Cell(row, 12).Value = line.Balance;
            ws.Cell(row, 12).Style.NumberFormat.Format = money;
            if (line.Balance < 0)
            {
                ws.Cell(row, 12).Style.Font.FontColor = DangerRed;
            }

            row++;
        }

        var lastLineRow = row - 1;

        // No filter dropdowns, alone among these registers. The Balance column only means anything
        // in posting order, so a sort or a filter would leave every figure in it wrong while the
        // sheet still looked sound — the same reason the page refuses to make its table sortable.
        row = FinishTable(ws, headerRow, openingRow, row, cols, filter: false);

        // After FinishTable: the stripe is painted over the whole data range and would take this
        // fill with it.
        var opening = ws.Range(openingRow, 1, openingRow, cols);
        opening.Style.Fill.BackgroundColor = AccentBlue;
        opening.Style.Font.Italic = true;
        ws.Cell(openingRow, 12).Style.Font.Bold = true;

        if (ledger.Lines.Count == 0)
        {
            // FinishTable's own empty message cannot fire here, because the brought-forward line is
            // a row. Without this the sheet would show an opening balance and simply stop, which
            // reads as a truncated export rather than as a quiet month.
            row = WriteNotice(ws, row, cols,
                $"Nothing was posted to this account between {ledger.FromDate:dd MMM yyyy} and "
                + $"{ledger.ToDate:dd MMM yyyy}. The balance below is the one it carried in.",
                MutedText);
        }

        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 9).Value = "Closing balance";
        ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        WriteSubtotal(ws, row, 10, firstLineRow, lastLineRow, money);
        WriteSubtotal(ws, row, 11, firstLineRow, lastLineRow, money);
        ws.Cell(row, 12).Value = ledger.ClosingBalance;
        ws.Cell(row, 12).Style.NumberFormat.Format = money;
        StyleTotalsRow(ws, row, cols);
        row++;

        WriteFooter(ws, row - 1, cols);
        FinalizeSheet(ws, cols, headerRow, landscape: true);
        return WorkbookToBytes(workbook);
    }

    /// <summary>
    /// The money format for an account's own currency, so a figure read out of context still says
    /// what it is denominated in.
    /// </summary>
    /// <remarks>
    /// SAP writes "##" on an account that is not held to one currency. That falls through to the
    /// unlabelled format on purpose — naming a currency there would be a guess.
    /// </remarks>
    private static string MoneyFormatFor(string? currency) => currency?.Trim().ToUpperInvariant() switch
    {
        "USD" => FormatUsd,
        "ZWG" or "ZIG" or "ZWL" => FormatZig,
        _ => FormatMoney
    };

    private static XLColor ReconciliationColour(GLAccountLedgerResponse ledger) => ledger switch
    {
        { IsReconciled: false } => WarningOrange,
        { ReconciliationDifference: 0 } => SuccessGreen,
        _ => DangerRed
    };

    private static string ReconciliationNotice(GLAccountLedgerResponse ledger)
    {
        var sapBalance = ledger.SapBalance.ToString("N2", CultureInfo.InvariantCulture);
        var journalBalance = ledger.ComputedBalanceToday.ToString("N2", CultureInfo.InvariantCulture);
        var difference = Math.Abs(ledger.ReconciliationDifference).ToString("N2", CultureInfo.InvariantCulture);

        if (!ledger.IsReconciled)
        {
            return "SAP would not give up this account's own balance, so the totals here could not "
                + "be checked against it. The lines themselves are unaffected.";
        }

        if (ledger.ReconciliationDifference == 0)
        {
            return $"Agrees with SAP. Summing every journal line up to today gives {journalBalance}, "
                + "which is what SAP reports for this account.";
        }

        return $"These figures do not agree with SAP. SAP reports a balance of {sapBalance} for this "
            + $"account; summing every journal line up to today gives {journalBalance}, a difference "
            + $"of {difference}. Treat the balances here as unreliable until that is explained — "
            + "the individual lines are read directly from the journal and are not in question.";
    }
}
