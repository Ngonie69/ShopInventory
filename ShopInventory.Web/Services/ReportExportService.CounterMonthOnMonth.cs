using ClosedXML.Excel;
using ShopInventory.Web.Features.Reports.MonthOnMonth;

namespace ShopInventory.Web.Services;

/// <summary>The Shop &amp; Vending month-on-month workbook.</summary>
/// <remarks>
/// A Read me, a summary by place, one sheet per product group, cash vs plastic, and product lines. Every
/// table is written by <see cref="WriteMonthTable{T}"/> from a column list, so each month pair is stated the
/// same way everywhere: the earlier month, the later one, the change and the change as a percentage.
/// </remarks>
public partial class ReportExportService
{
    private sealed record MonthColumn<T>(string Header, Func<T, object?> Value, string? Format = null, bool Total = false);

    private const string FormatSignedMoneyMoM = "#,##0.00;[Red]-#,##0.00;\"-\"";
    private const string FormatSignedPercentMoM = "0.0%;[Red]-0.0%";

    public byte[] ExportCounterMonthOnMonthToExcel(CounterMonthOnMonthReport report)
    {
        using var workbook = NewWorkbook("Shop & Vending Month on Month");
        var p = report.PreviousLabel;
        var c = report.CurrentLabel;
        var scope = $"{report.CurrentLabel} compared with {report.PreviousLabel} · amounts in USD";

        WriteMonthReadMe(workbook, report, scope);

        WriteMonthTable(workbook, "Summary", "Takings by Shop and Vending Point (incl. VAT)", scope, report.Places,
        [
            new("Business", x => x.BusinessLabel),
            new("Shop / Vending Point", x => x.Name),
            new("On the Till Since", x => x.FirstSaleDate, FormatDate),
            new($"Days {p}", x => x.PreviousTradingDays, FormatCount),
            new($"Days {c}", x => x.CurrentTradingDays, FormatCount),
            new($"Takings {p}", x => x.Takings.Previous, FormatMoney, true),
            new($"Takings {c}", x => x.Takings.Current, FormatMoney, true),
            new($"Credits {p}", x => x.Credits.Previous, FormatMoney, true),
            new($"Credits {c}", x => x.Credits.Current, FormatMoney, true),
            new($"Net {p}", x => x.NetTakings.Previous, FormatMoney, true),
            new($"Net {c}", x => x.NetTakings.Current, FormatMoney, true),
            new("Change", x => x.NetTakings.Change, FormatSignedMoneyMoM, true),
            new("Change %", x => Percent(x.NetTakings), FormatSignedPercentMoM),
        ]);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Read me", "Summary", "Cash vs Plastic", "Product Lines" };
        foreach (var group in report.Groups)
        {
            var sheet = UniqueSheetName(group.Name, usedNames);
            var subtitle = $"{scope} · {group.MembersSummary}";
            var ws = WriteMonthTable(workbook, sheet, $"{group.Name}: by Shop and Vending Point (before VAT)", subtitle, group.Places,
            [
                new("Business", x => x.Place.BusinessLabel),
                new("Shop / Vending Point", x => x.Place.Name),
                new($"Qty {p}", x => x.Quantity.Previous, FormatQuantity, true),
                new($"Qty {c}", x => x.Quantity.Current, FormatQuantity, true),
                new($"Value {p}", x => x.Value.Previous, FormatMoney, true),
                new($"Value {c}", x => x.Value.Current, FormatMoney, true),
                new("Change", x => x.Value.Change, FormatSignedMoneyMoM, true),
                new("Change %", x => Percent(x.Value), FormatSignedPercentMoM),
                new($"Share {p}", x => x.Share.Previous, FormatPercent),
                new($"Share {c}", x => x.Share.Current, FormatPercent),
            ], footer: false);

            AppendMonthTable(ws, "By Item", group.Items,
            [
                new("Business", x => x.Place.BusinessLabel),
                new("Shop / Vending Point", x => x.Place.Name),
                new("Item Code", x => x.ItemCode),
                new("Item", x => x.ItemName),
                new($"Qty {p}", x => x.Quantity.Previous, FormatQuantity, true),
                new($"Qty {c}", x => x.Quantity.Current, FormatQuantity, true),
                new($"Value {p}", x => x.Value.Previous, FormatMoney, true),
                new($"Value {c}", x => x.Value.Current, FormatMoney, true),
                new("Change", x => x.Value.Change, FormatSignedMoneyMoM, true),
                new("Change %", x => Percent(x.Value), FormatSignedPercentMoM),
            ]);
        }

        var tenderWs = WriteMonthTable(workbook, "Cash vs Plastic", "Cash vs Plastic Money (incl. VAT)",
            $"{scope} · plastic = Swipe (card) · mobile = Ecocash + Innbucks", report.Tenders,
        [
            new("Business", x => x.Place.BusinessLabel),
            new("Shop / Vending Point", x => x.Place.Name),
            new($"Days {p}", x => x.Place.PreviousTradingDays, FormatCount),
            new($"Days {c}", x => x.Place.CurrentTradingDays, FormatCount),
            new($"Cash {p}", x => x.Cash.Previous, FormatMoney, true),
            new($"Cash {c}", x => x.Cash.Current, FormatMoney, true),
            new($"Plastic {p}", x => x.Plastic.Previous, FormatMoney, true),
            new($"Plastic {c}", x => x.Plastic.Current, FormatMoney, true),
            new($"Mobile {p}", x => x.Mobile.Previous, FormatMoney, true),
            new($"Mobile {c}", x => x.Mobile.Current, FormatMoney, true),
            new($"Other {p}", x => x.Other.Previous, FormatMoney, true),
            new($"Other {c}", x => x.Other.Current, FormatMoney, true),
            new($"Cash Share {p}", x => x.CashShare.Previous, FormatPercent),
            new($"Cash Share {c}", x => x.CashShare.Current, FormatPercent),
            new($"Plastic Share {p}", x => x.PlasticShare.Previous, FormatPercent),
            new($"Plastic Share {c}", x => x.PlasticShare.Current, FormatPercent),
            new($"Plastic per Day {p}", x => PerDay(x.Plastic.Previous, x.Place.PreviousTradingDays), FormatMoney),
            new($"Plastic per Day {c}", x => PerDay(x.Plastic.Current, x.Place.CurrentTradingDays), FormatMoney),
            new($"Credits {p}", x => x.Credits.Previous, FormatMoney, true),
            new($"Credits {c}", x => x.Credits.Current, FormatMoney, true),
        ], footer: false);

        AppendMonthTable(tenderWs, "Number of Sales", report.Tenders,
        [
            new("Business", x => x.Place.BusinessLabel),
            new("Shop / Vending Point", x => x.Place.Name),
            new($"Cash {p}", x => x.CashSales.Previous, FormatCount, true),
            new($"Cash {c}", x => x.CashSales.Current, FormatCount, true),
            new($"Plastic {p}", x => x.PlasticSales.Previous, FormatCount, true),
            new($"Plastic {c}", x => x.PlasticSales.Current, FormatCount, true),
            new($"Mobile {p}", x => x.MobileSales.Previous, FormatCount, true),
            new($"Mobile {c}", x => x.MobileSales.Current, FormatCount, true),
            new($"Other {p}", x => x.OtherSales.Previous, FormatCount, true),
            new($"Other {c}", x => x.OtherSales.Current, FormatCount, true),
        ], note: report.OtherTenders.Count == 0 ? null : $"Other = {string.Join(", ", report.OtherTenders)}.");

        WriteMonthTable(workbook, "Product Lines", "Product Lines (SAP item groups, before VAT)", scope, report.ProductLines,
        [
            new("Business", x => x.Business == CounterMonthOnMonthReportBuilder.Vending ? "Vending" : "Shops"),
            new("Shop / Vending Point", x => x.Place?.Name ?? (x.Business == CounterMonthOnMonthReportBuilder.Vending ? "All vending" : "All shops")),
            new("Product Line", x => x.ProductLine),
            new($"Qty {p}", x => x.Quantity.Previous, FormatQuantity),
            new($"Qty {c}", x => x.Quantity.Current, FormatQuantity),
            new($"Value {p}", x => x.Value.Previous, FormatMoney),
            new($"Value {c}", x => x.Value.Current, FormatMoney),
            new("Change", x => x.Value.Change, FormatSignedMoneyMoM),
            new("Change %", x => Percent(x.Value), FormatSignedPercentMoM),
            new($"Share {p}", x => x.Share.Previous, FormatPercent),
            new($"Share {c}", x => x.Share.Current, FormatPercent),
        ], totals: false);

        return WorkbookToBytes(workbook);
    }

    private static object? Percent(MonthPair pair) => pair.ChangeFraction is { } f ? f : pair.IsNew ? "new" : null;

    private static decimal? PerDay(decimal amount, int days) => days == 0 ? null : amount / days;

    private static string UniqueSheetName(string name, HashSet<string> used)
    {
        var clean = new string(name.Select(ch => "[]:*?/\\".Contains(ch) ? '-' : ch).ToArray()).Trim();
        if (clean.Length == 0)
        {
            clean = "Group";
        }

        var candidate = clean.Length > 31 ? clean[..31] : clean;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var suffix = $" ({n})";
            candidate = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }

        return candidate;
    }

    private static void WriteMonthReadMe(XLWorkbook workbook, CounterMonthOnMonthReport report, string scope)
    {
        const int cols = 2;
        var ws = AddSheet(workbook, "Read me");
        var row = WriteReportHeader(ws, "Shop & Vending: Month on Month", cols, report.PreviousMonth, report.CurrentMonth.AddMonths(1).AddDays(-1), scope);

        var lines = new List<(string, string)>
        {
            ("Source", "Till and vending sales captured in ShopInventory (Shop & Vending Sales). Van sales are not included."),
            ("Months", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{report.PreviousMonth:MMMM yyyy} and {report.CurrentMonth:MMMM yyyy}, by sale date.")),
            ("Takings", "Sale totals including VAT, the money taken. Credits are till credit notes against those sales; Net is takings less credits."),
            ("Item values", "Product groups and product lines use sale lines, which are before VAT, with credited items already taken off."),
            ("Cash vs plastic", "Plastic money is the card reader (Swipe). Mobile money is Ecocash and Innbucks. Compare shares and per-day figures when a month is partial."),
            ("Product groups", "Each group is kept on the Product Groups page. A group applies its current products to both months, so they are measured on the same products."),
            ("Product line", "The item's SAP item group, from the product master."),
            ("Change %", "\"new\" means nothing was sold in the earlier month; blank means there is nothing to compare."),
        };
        foreach (var group in report.Groups)
        {
            lines.Add(($"Group: {group.Name}", string.IsNullOrWhiteSpace(group.Description) ? group.MembersSummary : $"{group.Description} ({group.MembersSummary})"));
        }

        foreach (var (label, text) in lines)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ws.Cell(row, 2).Value = text;
            ws.Cell(row, 2).Style.Alignment.WrapText = true;
            row++;
        }

        if (report.Notes.Count > 0)
        {
            row++;
            ws.Cell(row, 1).Value = "Before comparing";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontColor = WarningOrange;
            foreach (var note in report.Notes)
            {
                ws.Cell(row, 2).Value = note;
                ws.Cell(row, 2).Style.Alignment.WrapText = true;
                ws.Cell(row, 2).Style.Font.FontColor = WarningOrange;
                row++;
            }
        }

        WriteFooter(ws, row + 1, cols);
        ws.Column(1).Width = 26;
        ws.Column(2).Width = 110;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0); // one page wide: the label and its text print side by side
    }

    private static IXLWorksheet WriteMonthTable<T>(
        XLWorkbook workbook, string sheet, string title, string subtitle, IReadOnlyList<T> rows,
        IReadOnlyList<MonthColumn<T>> columns, bool totals = true, bool footer = true)
    {
        var ws = AddSheet(workbook, sheet);
        var row = WriteReportHeader(ws, title, columns.Count, null, null, subtitle);
        var headerRow = row;
        row = WriteMonthRows(ws, row, rows, columns, totals);
        if (footer)
        {
            // A sheet with no second table is finished here. One that gets a second table is finished by
            // AppendMonthTable instead: finalising twice prints the page header twice.
            WriteFooter(ws, row + 1, columns.Count);
            FinalizeSheet(ws, columns.Count, headerRow, landscape: columns.Count > 8);
        }
        else
        {
            ws.SheetView.FreezeRows(headerRow);
        }

        return ws;
    }

    private static void AppendMonthTable<T>(
        IXLWorksheet ws, string heading, IReadOnlyList<T> rows, IReadOnlyList<MonthColumn<T>> columns, string? note = null)
    {
        var row = (ws.LastRowUsed()?.RowNumber() ?? 0) + 3;
        ws.Cell(row, 1).Value = heading;
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 12;
        ws.Cell(row, 1).Style.Font.FontColor = NavyBlue;
        row = WriteMonthRows(ws, row + 1, rows, columns, totals: true);
        if (note is not null)
        {
            ws.Cell(row, 1).Value = note;
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = MutedText;
            row++;
        }

        var lastCol = Math.Max(columns.Count, ws.LastColumnUsed()?.ColumnNumber() ?? 1);
        WriteFooter(ws, row + 1, lastCol);

        // Measured again now the second table is on the sheet, or its wider columns (an item name) keep the
        // first table's widths and print cut short.
        FinalizeSheet(ws, lastCol, ws.SheetView.SplitRow, landscape: lastCol > 8);
    }

    /// <summary>Writes a header, the rows and (optionally) a SUBTOTAL totals row; returns the next free row.</summary>
    private static int WriteMonthRows<T>(IXLWorksheet ws, int row, IReadOnlyList<T> rows, IReadOnlyList<MonthColumn<T>> columns, bool totals)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            ws.Cell(row, i + 1).Value = columns[i].Header;
        }

        StyleTableHeader(ws, row, columns.Count);
        var headerRow = row++;
        var dataStart = row;

        foreach (var item in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var cell = ws.Cell(row, i + 1);
                switch (columns[i].Value(item))
                {
                    case null:
                        break;
                    case string s:
                        cell.Value = s;
                        if (s == "new")
                        {
                            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        }
                        break;
                    case DateTime d:
                        cell.Value = d;
                        break;
                    case int n:
                        cell.Value = n;
                        break;
                    case decimal m:
                        cell.Value = m;
                        break;
                    case var other:
                        cell.Value = other.ToString();
                        break;
                }

                if (columns[i].Format is { } format)
                {
                    cell.Style.NumberFormat.Format = format;
                }
            }

            row++;
        }

        var lastData = row - 1;
        row = FinishTable(ws, headerRow, dataStart, row, columns.Count, "Nothing was sold in either month.", filter: true);

        if (totals && lastData >= dataStart)
        {
            ws.Cell(row, 1).Value = "Total";
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].Total)
                {
                    WriteSubtotal(ws, row, i + 1, dataStart, lastData, columns[i].Format ?? FormatMoney);
                }
            }

            StyleTotalsRow(ws, row, columns.Count);
            row++;
        }

        return row;
    }
}
