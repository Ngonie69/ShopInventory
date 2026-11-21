using ClosedXML.Excel;
using ShopInventory.Web.Common;

namespace ShopInventory.Web.Services;

/// <summary>The Stock in Hand workbook: one warehouse, every item, now or at a past moment.</summary>
/// <remarks>
/// <para>Every item is written, whatever chip or search the page is narrowed to; the Status column and the
/// filter on the header row narrow it again in Excel.</para>
///
/// <para>No quantity is totalled. Each item counts in its own unit, so a sum down the column adds kilograms to
/// boxes; the cards above the table count items instead, as the page does.</para>
/// </remarks>
public partial class ReportExportService
{
    public byte[] ExportStockInHandToExcel(StockInHandExport export)
    {
        var past = export.AsAtUtc is not null;
        var rows = export.Rows;
        const int cols = 7;

        var warehouse = string.IsNullOrWhiteSpace(export.WarehouseName)
                        || export.WarehouseName.Equals(export.WarehouseCode, StringComparison.OrdinalIgnoreCase)
            ? export.WarehouseCode
            : $"{export.WarehouseName} ({export.WarehouseCode})";
        var moment = export.AsAtUtc is { } at
            ? $"As at {IAuditService.ToCAT(at):ddd dd MMM yyyy, HH:mm} CAT, rebuilt from the stock ledger, against now"
            : "Now, as SAP holds it";

        using var workbook = NewWorkbook("Stock in Hand");
        var ws = AddSheet(workbook, "Stock in Hand");
        var row = WriteReportHeader(ws, $"Stock in Hand · {warehouse}", cols, subtitle: moment);

        if (past)
        {
            WriteKpiCard(ws, row, 1, "On the Shelf Then", rows.Count(r => r.Then > 0), FormatCount);
            WriteKpiCard(ws, row, 2, "At Zero Then", rows.Count(r => r.Then <= 0), FormatCount);
            WriteKpiCard(ws, row, 3, "Up Since", rows.Count(r => r.Change > 0), FormatCount, SuccessGreen);
            WriteKpiCard(ws, row, 4, "Down Since", rows.Count(r => r.Change < 0), FormatCount);
            WriteKpiCard(ws, row, 5, "Items Held", rows.Count, FormatCount);
        }
        else
        {
            var oversold = rows.Count(r => r.Free < 0);
            WriteKpiCard(ws, row, 1, "On the Shelf", rows.Count(r => r.Now > 0), FormatCount);
            WriteKpiCard(ws, row, 2, "At Zero", rows.Count(r => r.Now <= 0), FormatCount);
            WriteKpiCard(ws, row, 3, "Oversold", oversold, FormatCount, oversold > 0 ? DangerRed : SuccessGreen);
            WriteKpiCard(ws, row, 4, "Items Held", rows.Count, FormatCount);
        }
        row += 3;

        var headers = past
            ? new[] { "Item Code", "Item", "UoM", "On Hand Then", "On Hand Now", "Change", "Status" }
            : new[] { "Item Code", "Item", "UoM", "On Hand", "Committed", "Free to Sell", "Status" };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row;
        row++;

        var dataStart = row;
        var flagged = new List<(int Row, XLColor Colour)>();
        foreach (var item in rows)
        {
            ws.Cell(row, 1).Value = item.Code;
            ws.Cell(row, 2).Value = item.Name;
            ws.Cell(row, 3).Value = item.UoM ?? "";
            ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            string status;
            if (past)
            {
                ws.Cell(row, 4).Value = item.Then;
                ws.Cell(row, 5).Value = item.Now;
                ws.Cell(row, 6).Value = item.Change;
                ws.Cell(row, 6).Style.NumberFormat.Format = FormatSignedQuantity;
                status = item.Change switch { > 0 => "Up since", < 0 => "Down since", _ => "Unchanged" };
            }
            else
            {
                ws.Cell(row, 4).Value = item.Now;
                ws.Cell(row, 5).Value = item.Committed;
                ws.Cell(row, 6).Value = item.Free;
                ws.Cell(row, 6).Style.NumberFormat.Format = FormatFreeToSell;
                status = item.Free < 0 ? "Oversold" : item.Now <= 0 ? "At zero" : "On the shelf";
                if (item.Free < 0) flagged.Add((row, DangerRed));
                else if (item.Now <= 0) flagged.Add((row, WarningOrange));
            }

            ws.Range(row, 4, row, 5).Style.NumberFormat.Format = FormatQuantity;
            ws.Cell(row, 7).Value = status;
            row++;
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, "This warehouse holds no items.");

        // After FinishTable, which restyles the rows it stripes.
        foreach (var (flaggedRow, colour) in flagged)
        {
            ws.Cell(flaggedRow, 7).Style.Font.FontColor = colour;
            ws.Cell(flaggedRow, 7).Style.Font.Bold = true;
        }

        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, headerRow, fitRows: 300);
        return WorkbookToBytes(workbook);
    }

    // Free to sell goes below zero when more is promised than is on the shelf, and that minus is the point.
    private const string FormatFreeToSell = "#,##0.00;[Red]-#,##0.00";
}
