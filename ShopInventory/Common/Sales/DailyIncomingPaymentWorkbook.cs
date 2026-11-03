using System.Globalization;
using ClosedXML.Excel;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The workbook attached to the daily takings email: one row for every invoice the payment settles, so
/// the cash holders can count against it and finance can trace any invoice back to the sale it came from.
/// </summary>
/// <remarks>
/// The email lists only the first <see cref="DailyIncomingPaymentEmailBody.ListedInvoices"/>; this has
/// them all. Totals are <c>SUBTOTAL(109, …)</c> so they follow the filter, as the Web's report exports do.
/// Column widths are set, not measured: measuring is the slow part of ClosedXML and the columns are known.
/// </remarks>
public static class DailyIncomingPaymentWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The row the invoice table's headings sit on; the sheet is frozen beneath it.</summary>
    internal const int HeaderRow = 8;

    private const string MoneyFormat = "#,##0.00";
    private const string DateFormat = "dd mmm yyyy";

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#35374A");
    private static readonly XLColor LabelInk = XLColor.FromHtml("#4E5164");
    private static readonly XLColor Rule = XLColor.FromHtml("#E1E2E8");
    private static readonly XLColor Stripe = XLColor.FromHtml("#F6F6F9");
    private static readonly XLColor TotalFill = XLColor.FromHtml("#ECECF3");

    private static readonly char[] Unsafe = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>The attachment's name: the day and the partner, which is what a person searches their mail for.</summary>
    public static string FileName(DailyIncomingPaymentEntity payment, string partnerName)
    {
        var name = new string(partnerName.Select(c => Array.IndexOf(Unsafe, c) >= 0 ? ' ' : c).ToArray());
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return $"Daily takings {payment.PaymentDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)} - {name}.xlsx";
    }

    public static byte[] Build(DailyIncomingPaymentEntity payment, string partnerName, IReadOnlyList<DailyPaymentEmailLine> lines)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Invoices");
        sheet.Style.Font.FontName = "Calibri";
        sheet.Style.Font.FontSize = 11;

        var cash = lines.Sum(line => line.Cash);
        var electronic = lines.Sum(line => line.Electronic);

        sheet.Cell(1, 1).Value = $"Daily incoming payment · {partnerName}";
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(15);
        sheet.Row(1).Height = 22;
        sheet.Cell(2, 1).Value = $"{payment.PaymentDate.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)} · posted to SAP";
        sheet.Cell(2, 1).Style.Font.FontColor = LabelInk;

        Pair(sheet, 4, 1, "Reference", payment.Reference, bold: true);
        Pair(sheet, 4, 7, "SAP payment", payment.SapDocNum is { } docNum ? docNum : Blank.Value, bold: true);
        Pair(sheet, 5, 1, "Cash G/L", payment.CashAccount ?? "-");
        Pair(sheet, 5, 7, "Electronic G/L", payment.TransferAccount ?? "-");
        Pair(sheet, 6, 1, "Cash to hand in", cash, bold: true, money: true);
        Pair(sheet, 6, 7, "Ecocash, Innbucks & swipe", electronic, bold: true, money: true);

        string[] headings = ["#", "Invoice no.", "SAP DocEntry", "Invoice date", "Sale reference", "Source", "Cash", "Electronic", "Total"];
        for (var column = 0; column < headings.Length; column++)
        {
            sheet.Cell(HeaderRow, column + 1).Value = headings[column];
        }

        var header = sheet.Range(HeaderRow, 1, HeaderRow, headings.Length);
        header.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(HeaderFill);
        sheet.Range(HeaderRow, 1, HeaderRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        sheet.Range(HeaderRow, 7, HeaderRow, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        var row = HeaderRow + 1;
        for (var index = 0; index < lines.Count; index++, row++)
        {
            var line = lines[index];
            sheet.Cell(row, 1).Value = index + 1;
            sheet.Cell(row, 2).Value = line.InvoiceDocNum is { } number ? number : Blank.Value;
            sheet.Cell(row, 3).Value = line.InvoiceDocEntry;
            sheet.Cell(row, 4).Value = line.InvoiceDate is { } date ? date.Date : Blank.Value;
            sheet.Cell(row, 5).Value = line.SaleReference ?? string.Empty;
            sheet.Cell(row, 6).Value = line.Source ?? string.Empty;
            sheet.Cell(row, 7).Value = line.Cash;
            sheet.Cell(row, 8).Value = line.Electronic;
            sheet.Cell(row, 9).Value = line.Total;

            if (index % 2 == 1)
            {
                sheet.Range(row, 1, row, headings.Length).Style.Fill.BackgroundColor = Stripe;
            }
        }

        var lastData = row - 1;
        if (lines.Count > 0)
        {
            var body = sheet.Range(HeaderRow + 1, 1, lastData, headings.Length);
            body.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            body.Style.Border.BottomBorderColor = Rule;
            sheet.Range(HeaderRow + 1, 4, lastData, 4).Style.NumberFormat.Format = DateFormat;
            sheet.Range(HeaderRow + 1, 4, lastData, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            sheet.Range(HeaderRow + 1, 2, lastData, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        }

        sheet.Cell(row, 2).Value = $"Total · {lines.Count.ToString("N0", CultureInfo.InvariantCulture)} invoice{(lines.Count == 1 ? string.Empty : "s")}";
        for (var column = 7; column <= 9; column++)
        {
            var letter = XLHelper.GetColumnLetterFromNumber(column);
            sheet.Cell(row, column).FormulaA1 = lines.Count == 0
                ? "0"
                : $"SUBTOTAL(109,{letter}{HeaderRow + 1}:{letter}{lastData})";
        }

        var totals = sheet.Range(row, 1, row, headings.Length);
        totals.Style.Font.Bold = true;
        totals.Style.Fill.BackgroundColor = TotalFill;
        totals.Style.Border.TopBorder = XLBorderStyleValues.Medium;
        totals.Style.Border.TopBorderColor = HeaderFill;

        sheet.Range(HeaderRow + 1, 7, row, 9).Style.NumberFormat.Format = MoneyFormat;
        sheet.Range(HeaderRow, 1, Math.Max(lastData, HeaderRow), headings.Length).SetAutoFilter();

        double[] widths = [6, 13, 14, 14, 36, 14, 13, 13, 14];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }

        sheet.SheetView.FreezeRows(HeaderRow);
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(HeaderRow, HeaderRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>A label in one column and its value two columns on, as the header block lays them out.</summary>
    private static void Pair(IXLWorksheet sheet, int row, int column, string label, XLCellValue value, bool bold = false, bool money = false)
    {
        var labelCell = sheet.Cell(row, column);
        labelCell.Value = label;
        labelCell.Style.Font.FontColor = LabelInk;

        var valueCell = sheet.Cell(row, column + 2);
        valueCell.Value = value;
        valueCell.Style.Font.Bold = bold;
        valueCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        if (money)
        {
            valueCell.Style.NumberFormat.Format = MoneyFormat;
        }
    }
}
