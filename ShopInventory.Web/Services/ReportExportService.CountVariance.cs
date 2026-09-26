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
