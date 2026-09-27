using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>The count variance workbook: one SAP count, every line, valued at selling price.</summary>
/// <remarks>
/// Every line is written, whatever the page is filtered to, because the file is what gets argued over
/// afterwards. Unpriced and uncounted lines keep empty cells rather than zeros, as the page shows a dash:
/// neither has a value yet, and a zero would read as a clean line.
/// </remarks>
public partial class ReportExportService
{
    private const int CountVarianceColumns = 10;

    public byte[] ExportCountVarianceToExcel(CountVarianceReport report)
    {
        var document = report.Document;
        var currency = string.IsNullOrWhiteSpace(report.Currency) ? string.Empty : report.Currency + " ";
        var title = $"Count Variance — No. {document.DocumentNumber}";

        using var workbook = NewWorkbook(title);
        var ws = AddSheet(workbook, "Count Variance", NavyBlue);

        var subtitle = string.Join(" · ", new[]
        {
            string.Join(", ", report.Warehouses),
            document.CountDate is { } date ? $"counted {date:dd MMM yyyy} {document.CountTime}".TrimEnd() : null,
            document.CounterName is { } counter ? $"by {counter}" : null,
            document.Status,
            $"valued at {report.PriceListName ?? $"price list {report.PriceListNum}"} excl. VAT"
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

        var row = WriteReportHeader(ws, title, CountVarianceColumns, subtitle: subtitle);

        var totals = report.Totals;
        WriteKpiCard(ws, row, 1, "Net variance", totals.NetValue, FormatMoney, totals.NetValue < 0 ? DangerRed : SuccessGreen);
        WriteKpiCard(ws, row, 2, "Short", totals.ShortValue, FormatMoney, DangerRed);
        WriteKpiCard(ws, row, 3, "Over", totals.OverValue, FormatMoney, SuccessGreen);
        WriteKpiCard(ws, row, 4, "Stock value", totals.StockValue, FormatMoney);
        WriteKpiCard(ws, row, 5, "Not valued", totals.UnvaluedVarianceLines, FormatCount, WarningOrange);
        WriteKpiCard(ws, row, 6, "Not counted", totals.NotCountedLines, FormatCount);
        row += 3;

        string[] headers =
        [
            "Row", "Item", "Description", "Warehouse", "In warehouse", "Counted", "Variance",
            $"{currency}Selling price".Trim(), $"{currency}Variance value".Trim(), "Status"
        ];
        for (var col = 0; col < headers.Length; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }

        StyleTableHeader(ws, row, CountVarianceColumns);
        var freezeAt = row;
        row++;
        var dataStart = row;

        foreach (var line in report.Lines)
        {
            ws.Cell(row, 1).Value = line.RowNumber;
            ws.Cell(row, 2).Value = line.ItemCode;
            ws.Cell(row, 3).Value = line.ItemDescription;
            ws.Cell(row, 4).Value = line.WarehouseCode;
            ws.Cell(row, 5).Value = line.InWarehouseQuantity;
            ws.Cell(row, 5).Style.NumberFormat.Format = FormatQuantity;

            if (line.CountedQuantity is { } countedQuantity)
            {
                ws.Cell(row, 6).Value = countedQuantity;
                ws.Cell(row, 6).Style.NumberFormat.Format = FormatQuantity;
            }

            // An uncounted line has no variance yet, only SAP's placeholder zero.
            var counted = line.CountedQuantity is not null;
            if (counted)
            {
                ws.Cell(row, 7).Value = line.Variance;
                ws.Cell(row, 7).Style.NumberFormat.Format = FormatQuantity;
            }

            if (line.SellingPrice is { } price)
            {
                ws.Cell(row, 8).Value = price;
                ws.Cell(row, 8).Style.NumberFormat.Format = FormatMoney;
            }

            if (counted && line.VarianceValue is { } value)
            {
                ws.Cell(row, 9).Value = value;
                ws.Cell(row, 9).Style.NumberFormat.Format = FormatMoney;
                ws.Cell(row, 9).Style.Font.Bold = value != 0;
            }

            ws.Cell(row, 10).Value = CountVarianceStatusText(line);
            row++;
        }

        var lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, CountVarianceColumns, "This count has no lines.");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 5, dataStart, lastData, FormatQuantity);
        WriteSubtotal(ws, row, 7, dataStart, lastData, FormatQuantity);
        WriteSubtotal(ws, row, 9, dataStart, lastData, FormatMoney);
        StyleTotalsRow(ws, row, CountVarianceColumns);

        WriteFooter(ws, row, CountVarianceColumns);
        FinalizeSheet(ws, CountVarianceColumns, freezeAt, landscape: true);

        return WorkbookToBytes(workbook);
    }

    /// <summary>Every van's latest count in the range: a sheet by van, one by item, one of vans not counted.</summary>
    public byte[] ExportVanCountVarianceToExcel(VanCountVarianceReport report)
    {
        var currency = string.IsNullOrWhiteSpace(report.Currency) ? string.Empty : report.Currency + " ";
        var title = "Van Count Variance";
        var subtitle = $"Counts dated {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy} · "
            + $"{report.Vans.Count} of {report.VanCount} vans counted · "
            + $"valued at {report.PriceListName ?? $"price list {report.PriceListNum}"} excl. VAT"
            + (report.Truncated ? " · TRUNCATED: narrow the range" : string.Empty);

        using var workbook = NewWorkbook(title);

        // ── By van ──
        const int vanColumns = 11;
        var ws = AddSheet(workbook, "By Van", NavyBlue);
        var row = WriteReportHeader(ws, title, vanColumns, subtitle: subtitle);

        var totals = report.Totals;
        WriteKpiCard(ws, row, 1, "Net variance", totals.NetValue, FormatMoney, totals.NetValue < 0 ? DangerRed : SuccessGreen);
        WriteKpiCard(ws, row, 2, "Short", totals.ShortValue, FormatMoney, DangerRed);
        WriteKpiCard(ws, row, 3, "Over", totals.OverValue, FormatMoney, SuccessGreen);
        WriteKpiCard(ws, row, 4, "Stock value", totals.StockValue, FormatMoney);
        WriteKpiCard(ws, row, 5, "Vans counted", report.Vans.Count, FormatCount);
        WriteKpiCard(ws, row, 6, "Not counted", report.VansNotCounted.Count, FormatCount, WarningOrange);
        row += 3;

        string[] vanHeaders =
        [
            "Van", "Rep", "Count No.", "Count date", "Status", "Replaces", "Lines",
            $"{currency}Short".Trim(), $"{currency}Over".Trim(), $"{currency}Net variance".Trim(), "Lines not valued"
        ];
        for (var col = 0; col < vanHeaders.Length; col++)
        {
            ws.Cell(row, col + 1).Value = vanHeaders[col];
        }

        StyleTableHeader(ws, row, vanColumns);
        var freezeAt = row;
        row++;
        var dataStart = row;

        foreach (var van in report.Vans)
        {
            ws.Cell(row, 1).Value = van.WarehouseCode;
            ws.Cell(row, 2).Value = van.RepName;
            ws.Cell(row, 3).Value = van.Document.DocumentNumber;
            if (van.Document.CountDate is { } countDate)
            {
                ws.Cell(row, 4).Value = countDate;
                ws.Cell(row, 4).Style.NumberFormat.Format = FormatDate;
            }

            ws.Cell(row, 5).Value = van.Document.Status;
            ws.Cell(row, 6).Value = string.Join(", ", van.SupersededDocumentNumbers);
            ws.Cell(row, 7).Value = van.Totals.LineCount;
            ws.Cell(row, 7).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 8).Value = van.Totals.ShortValue;
            ws.Cell(row, 9).Value = van.Totals.OverValue;
            ws.Cell(row, 10).Value = van.Totals.NetValue;
            ws.Cell(row, 10).Style.Font.Bold = true;
            ws.Range(row, 8, row, 10).Style.NumberFormat.Format = FormatMoney;
            ws.Cell(row, 11).Value = van.Totals.UnvaluedVarianceLines;
            ws.Cell(row, 11).Style.NumberFormat.Format = FormatCount;
            row++;
        }

        var lastData = row - 1;
        row = FinishTable(ws, freezeAt, dataStart, row, vanColumns, "No van has a count dated in this range.");

        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 7, dataStart, lastData, FormatCount);
        WriteSubtotal(ws, row, 8, dataStart, lastData, FormatMoney);
        WriteSubtotal(ws, row, 9, dataStart, lastData, FormatMoney);
        WriteSubtotal(ws, row, 10, dataStart, lastData, FormatMoney);
        WriteSubtotal(ws, row, 11, dataStart, lastData, FormatCount);
        StyleTotalsRow(ws, row, vanColumns);
        WriteFooter(ws, row, vanColumns);
        FinalizeSheet(ws, vanColumns, freezeAt, landscape: true);

        // ── By item ──
        const int itemColumns = 9;
        var items = AddSheet(workbook, "By Item", NavyBlue);
        row = WriteReportHeader(items, "Van Count Variance by Item", itemColumns, subtitle: subtitle);

        string[] itemHeaders =
        [
            "Item", "Description", "Vans short", "Vans over", "Units short", "Units over", "Net units",
            $"{currency}Selling price".Trim(), $"{currency}Net value".Trim()
        ];
        for (var col = 0; col < itemHeaders.Length; col++)
        {
            items.Cell(row, col + 1).Value = itemHeaders[col];
        }

        StyleTableHeader(items, row, itemColumns);
        freezeAt = row;
        row++;
        dataStart = row;

        foreach (var item in report.Items)
        {
            items.Cell(row, 1).Value = item.ItemCode;
            items.Cell(row, 2).Value = item.ItemDescription;
            items.Cell(row, 3).Value = item.VansShort;
            items.Cell(row, 4).Value = item.VansOver;
            items.Cell(row, 5).Value = item.ShortQuantity;
            items.Cell(row, 6).Value = item.OverQuantity;
            items.Cell(row, 7).Value = item.NetQuantity;
            items.Range(row, 3, row, 4).Style.NumberFormat.Format = FormatCount;
            items.Range(row, 5, row, 7).Style.NumberFormat.Format = FormatQuantity;

            if (item.SellingPrice is { } price)
            {
                items.Cell(row, 8).Value = price;
                items.Cell(row, 8).Style.NumberFormat.Format = FormatMoney;
            }

            if (item.NetValue is { } value)
            {
                items.Cell(row, 9).Value = value;
                items.Cell(row, 9).Style.NumberFormat.Format = FormatMoney;
                items.Cell(row, 9).Style.Font.Bold = true;
            }
            else
            {
                items.Cell(row, 9).Value = "No price";
            }

            row++;
        }

        lastData = row - 1;
        row = FinishTable(items, freezeAt, dataStart, row, itemColumns, "No item came up short or over on any van.");

        items.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(items, row, 5, dataStart, lastData, FormatQuantity);
        WriteSubtotal(items, row, 6, dataStart, lastData, FormatQuantity);
        WriteSubtotal(items, row, 7, dataStart, lastData, FormatQuantity);
        WriteSubtotal(items, row, 9, dataStart, lastData, FormatMoney);
        StyleTotalsRow(items, row, itemColumns);
        WriteFooter(items, row, itemColumns);
        FinalizeSheet(items, itemColumns, freezeAt, landscape: true);

        // ── Not counted ──
        const int missingColumns = 2;
        var missing = AddSheet(workbook, "Not Counted", WarningOrange);
        row = WriteReportHeader(missing, "Vans with no count in the range", missingColumns, subtitle: subtitle);
        missing.Cell(row, 1).Value = "Van";
        missing.Cell(row, 2).Value = "Rep";
        StyleTableHeader(missing, row, missingColumns);
        freezeAt = row;
        row++;
        dataStart = row;

        foreach (var van in report.VansNotCounted)
        {
            missing.Cell(row, 1).Value = van.WarehouseCode;
            missing.Cell(row, 2).Value = van.RepName;
            row++;
        }

        row = FinishTable(missing, freezeAt, dataStart, row, missingColumns, "Every van has a count dated in this range.");
        WriteFooter(missing, row, missingColumns);
        FinalizeSheet(missing, missingColumns, freezeAt);

        return WorkbookToBytes(workbook);
    }

    private static string CountVarianceStatusText(CountVarianceLine line)
    {
        var status = line.Status switch
        {
            CountVarianceLineStatus.NotCounted => "Not counted",
            _ => line.Status
        };

        return line.SellingPrice is null ? $"{status} · no price" : status;
    }
}
