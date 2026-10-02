using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>The market breakages workbook: one view of the office list, every page of it.</summary>
/// <remarks>
/// The rep's figures are written as reported and the office's count beside them, as the page shows them.
/// A report the office has not counted keeps empty Counted and Difference cells rather than zeros: a zero
/// would read as "nothing came off the van". The totals, by van and by product are the API's, so this
/// file and the PDF of the same view agree.
/// </remarks>
public partial class ReportExportService
{
    /// <summary>Counted less reported: a shortfall in red, an excess with its plus sign.</summary>
    private const string FormatSignedQuantity = "+#,##0.00;[Red]-#,##0.00;0.00";

    public byte[] ExportMarketBreakagesToExcel(MarketBreakageExportDto export)
    {
        var title = $"Market Breakages — {MarketBreakageStatus.ScopeLabel(export.Status)}";
        var subtitle = string.Join(" · ", new[]
        {
            $"{export.TotalCount:N0} {(export.TotalCount == 1 ? "report" : "reports")}",
            export.Search is { } search ? $"matching \"{search}\"" : null,
            "times are CAT",
            export.Truncated ? $"TRUNCATED: only the newest {export.Reports.Count:N0}, narrow the view" : null
        }.Where(part => part is not null));

        using var workbook = NewWorkbook(title);
        WriteBreakageReportsSheet(workbook, export, title, subtitle);
        WriteBreakageLinesSheet(workbook, export, subtitle);
        WriteBreakageGroupSheet(workbook, "By Van", "Market Breakages by Van", subtitle, "Van", "Reps", export.ByVan);
        WriteBreakageGroupSheet(workbook, "By Product", "Market Breakages by Product", subtitle, "Item", "Description", export.ByProduct);

        return WorkbookToBytes(workbook);
    }

    private static void WriteBreakageReportsSheet(XLWorkbook workbook, MarketBreakageExportDto export, string title, string subtitle)
    {
        const int columns = 17;
        var ws = AddSheet(workbook, "Reports", NavyBlue);
        var row = WriteReportHeader(ws, title, columns, subtitle: subtitle);

        var totals = export.Totals;
        WriteKpiCard(ws, row, 1, "Reports", totals.Reports, FormatCount);
        WriteKpiCard(ws, row, 2, "Waiting on the office", totals.OpenReports, FormatCount, totals.OpenReports > 0 ? WarningOrange : null);
        WriteKpiCard(ws, row, 3, "Units reported", totals.ReportedQuantity, FormatQuantity);
        WriteKpiCard(ws, row, 4, "Units counted", totals.CountedQuantity, FormatQuantity);
        WriteKpiCard(ws, row, 5, "Moved to returns", totals.TransferredQuantity, FormatQuantity, SuccessGreen);
        WriteKpiCard(ws, row, 6, "Rejected units", totals.RejectedQuantity, FormatQuantity);
        row += 3;

        string[] headers =
        [
            "Report", "Reported at", "Van", "Rep", "Shop code", "Shop", "Status", "Lines", "Reported", "Counted",
            "Difference", "SAP transfer", "Decided by", "Decided at", "Rep's remarks", "Office remarks", "Transfer error"
        ];
        for (var col = 0; col < headers.Length; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }

        StyleTableHeader(ws, row, columns);
        var freezeAt = row;
        row++;
        var dataStart = row;

        foreach (var report in export.Reports)
        {
            var reported = report.Lines.Sum(line => line.ReportedQuantity);
            var counted = report.Lines.Any(line => line.ConfirmedQuantity is not null)
                ? report.Lines.Sum(line => line.ConfirmedQuantity ?? 0m)
                : (decimal?)null;

            ws.Cell(row, 1).Value = report.Id;
            WriteCatStamp(ws.Cell(row, 2), report.CapturedAtUtc);
            ws.Cell(row, 3).Value = report.VanWarehouseCode;
            ws.Cell(row, 4).Value = report.ReportedByName;
            ws.Cell(row, 5).Value = report.CardCode ?? string.Empty;
            ws.Cell(row, 6).Value = report.CardName ?? (report.CardCode is null ? "In transit" : string.Empty);
            ws.Cell(row, 7).Value = BreakageStatusText(report.Status);
            ws.Cell(row, 8).Value = report.Lines.Count;
            ws.Cell(row, 8).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 9).Value = reported;
            ws.Cell(row, 9).Style.NumberFormat.Format = FormatQuantity;

            if (counted is { } countedQuantity)
            {
                ws.Cell(row, 10).Value = countedQuantity;
                ws.Cell(row, 10).Style.NumberFormat.Format = FormatQuantity;
                ws.Cell(row, 11).Value = countedQuantity - reported;
                ws.Cell(row, 11).Style.NumberFormat.Format = FormatSignedQuantity;
                ws.Cell(row, 11).Style.Font.Bold = countedQuantity != reported;
            }

            if (report.SapDocNum is { } docNum)
            {
                ws.Cell(row, 12).Value = docNum;
            }

            ws.Cell(row, 13).Value = report.DecidedByName ?? string.Empty;
            if (report.DecidedAtUtc is { } decidedAt)
            {
                WriteCatStamp(ws.Cell(row, 14), decidedAt);
            }

            ws.Cell(row, 15).Value = report.Remarks ?? string.Empty;
            ws.Cell(row, 16).Value = report.DecisionRemarks ?? string.Empty;
            ws.Cell(row, 17).Value = report.Status == MarketBreakageStatus.Transferred ? string.Empty : report.LastError ?? string.Empty;
            row++;
        }

        var lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, columns, "No breakage reports match this view.");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 8, dataStart, lastData, FormatCount);
        WriteSubtotal(ws, row, 9, dataStart, lastData, FormatQuantity);
        WriteSubtotal(ws, row, 10, dataStart, lastData, FormatQuantity);
        StyleTotalsRow(ws, row, columns);
        WriteFooter(ws, row, columns);
        FinalizeSheet(ws, columns, freezeAt, landscape: true);
    }

    private static void WriteBreakageLinesSheet(XLWorkbook workbook, MarketBreakageExportDto export, string subtitle)
    {
        const int columns = 12;
        var ws = AddSheet(workbook, "Lines", NavyBlue);
        var row = WriteReportHeader(ws, "Market Breakages by Line", columns, subtitle: subtitle);

        string[] headers =
        [
            "Report", "Reported at", "Van", "Rep", "Shop", "Status", "Item", "Description", "Reason",
            "Reported", "Counted", "Difference"
        ];
        for (var col = 0; col < headers.Length; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }

        StyleTableHeader(ws, row, columns);
        var freezeAt = row;
        row++;
        var dataStart = row;

        foreach (var report in export.Reports)
        {
            foreach (var line in report.Lines)
            {
                ws.Cell(row, 1).Value = report.Id;
                WriteCatStamp(ws.Cell(row, 2), report.CapturedAtUtc);
                ws.Cell(row, 3).Value = report.VanWarehouseCode;
                ws.Cell(row, 4).Value = report.ReportedByName;
                ws.Cell(row, 5).Value = report.CardName ?? report.CardCode ?? "In transit";
                ws.Cell(row, 6).Value = BreakageStatusText(report.Status);
                ws.Cell(row, 7).Value = line.ItemCode;
                ws.Cell(row, 8).Value = line.ItemDescription ?? string.Empty;
                ws.Cell(row, 9).Value = line.Reason ?? string.Empty;
                ws.Cell(row, 10).Value = line.ReportedQuantity;
                ws.Cell(row, 10).Style.NumberFormat.Format = FormatQuantity;

                if (line.ConfirmedQuantity is { } counted)
                {
                    ws.Cell(row, 11).Value = counted;
                    ws.Cell(row, 11).Style.NumberFormat.Format = FormatQuantity;
                    ws.Cell(row, 12).Value = counted - line.ReportedQuantity;
                    ws.Cell(row, 12).Style.NumberFormat.Format = FormatSignedQuantity;
                    ws.Cell(row, 12).Style.Font.Bold = counted != line.ReportedQuantity;
                }

                row++;
            }
        }

        var lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, columns, "No breakage reports match this view.");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 10, dataStart, lastData, FormatQuantity);
        WriteSubtotal(ws, row, 11, dataStart, lastData, FormatQuantity);
        StyleTotalsRow(ws, row, columns);
        WriteFooter(ws, row, columns);
        FinalizeSheet(ws, columns, freezeAt, landscape: true);
    }

    private static void WriteBreakageGroupSheet(
        XLWorkbook workbook, string sheetName, string title, string subtitle, string codeHeader, string nameHeader,
        List<MarketBreakageExportGroupDto> groups)
    {
        const int columns = 6;
        var ws = AddSheet(workbook, sheetName, NavyBlue);
        var row = WriteReportHeader(ws, title, columns, subtitle: subtitle);

        string[] headers = [codeHeader, nameHeader, "Reports", "Reported", "Counted", "Moved to returns"];
        for (var col = 0; col < headers.Length; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }

        StyleTableHeader(ws, row, columns);
        var freezeAt = row;
        row++;
        var dataStart = row;

        foreach (var group in groups)
        {
            ws.Cell(row, 1).Value = group.Code;
            ws.Cell(row, 2).Value = group.Name ?? string.Empty;
            ws.Cell(row, 3).Value = group.Reports;
            ws.Cell(row, 3).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 4).Value = group.ReportedQuantity;
            ws.Cell(row, 5).Value = group.CountedQuantity;
            ws.Cell(row, 6).Value = group.TransferredQuantity;
            ws.Range(row, 4, row, 6).Style.NumberFormat.Format = FormatQuantity;
            row++;
        }

        var lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, columns, "No breakage reports match this view.");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 4, dataStart, lastData, FormatQuantity);
        WriteSubtotal(ws, row, 5, dataStart, lastData, FormatQuantity);
        WriteSubtotal(ws, row, 6, dataStart, lastData, FormatQuantity);
        StyleTotalsRow(ws, row, columns);
        WriteFooter(ws, row, columns);
        FinalizeSheet(ws, columns, freezeAt);
    }

    private static void WriteCatStamp(IXLCell cell, DateTime utc)
    {
        cell.Value = IAuditService.ToCAT(EnsureUtc(utc));
        cell.Style.NumberFormat.Format = FormatTimestamp;
    }

    /// <summary>The page's word for a status: a pending report is one the office has to count.</summary>
    private static string BreakageStatusText(string status)
        => status == MarketBreakageStatus.Pending ? "To count" : MarketBreakageStatus.Describe(status);
}
