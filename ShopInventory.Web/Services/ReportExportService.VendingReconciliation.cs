using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The vending Reconciliation workbook: every cashier-day in the range, one row per currency, with
/// what the sales say beside what was counted and banked.
/// </summary>
/// <remarks>
/// <para>
/// One row per currency rather than a column pair per currency, so a column never holds two currencies
/// and the totals are one row per currency — a single sum down a mixed column is not a number in any.
/// </para>
/// <para>
/// A day nobody counted keeps its counted, banked and variance cells <em>blank</em>, not zero: a zero
/// there would read as a shortfall of the whole day's takings, which is exactly the number the view was
/// deferred to avoid.
/// </para>
/// </remarks>
public partial class ReportExportService
{
    private static readonly string[] VendingReconciliationHeaders =
    [
        "Date", "Depot", "Warehouse", "Cashier", "Username", "Status", "Currency", "Sales",
        "Invoiced", "Cash sales", "Opening float", "Counted", "Accounted", "Banked", "Variance",
        "Reason", "Query", "Counted by", "Approved by", "Approved at"
    ];

    public byte[] ExportVendingReconciliationToExcel(VendingReconciliationModel reconciliation, string scopeLabel)
    {
        const string title = "Vending reconciliation";
        using var workbook = NewWorkbook(title);
        var ws = AddSheet(workbook, "Cash-ups");
        var lastCol = VendingReconciliationHeaders.Length;

        var row = WriteReportHeader(ws, $"{title} — {scopeLabel}", lastCol, reconciliation.FromDate, reconciliation.ToDate);
        var counts = reconciliation.Counts;
        row = WriteNotice(ws, row, lastCol,
            $"{counts.CashierDays:N0} cashier-days: {counts.Approved:N0} approved, {counts.AwaitingApproval:N0} awaiting approval, " +
            $"{counts.Queried:N0} queried, {counts.NotCashedUp:N0} not cashed up.",
            LightNavy);
        row = WriteNotice(ws, row, lastCol,
            "Accounted is the cash counted less the opening float, plus the non-cash slips. Variance is Accounted less Invoiced: " +
            "negative is short. A day not cashed up has no counted figure and no variance.",
            LightNavy);
        row++;

        var headerRow = row;
        for (var col = 0; col < lastCol; col++)
        {
            ws.Cell(row, col + 1).Value = VendingReconciliationHeaders[col];
        }

        StyleTableHeader(ws, row, lastCol);
        row++;
        var firstDataRow = row;

        var lines = reconciliation.Rows
            .SelectMany(day => (day.Currencies.Count == 0
                    ? [new VendingReconciliationCurrencyModel { Currency = "USD" }]
                    : day.Currencies)
                .Select(currency => (Day: day, Currency: currency)))
            .ToList();

        var shortRows = new List<int>();
        foreach (var (day, currency) in lines)
        {
            var money = MoneyFormatFor(currency.Currency);
            var c = 1;
            ws.Cell(row, c).Value = day.BusinessDate;
            ws.Cell(row, c++).Style.NumberFormat.Format = FormatDate;
            ws.Cell(row, c++).Value = day.DepotName ?? day.BusinessPartnerCode ?? string.Empty;
            ws.Cell(row, c++).Value = day.WarehouseCode;
            ws.Cell(row, c++).Value = day.CashierName;
            ws.Cell(row, c++).Value = day.CashierUsername;
            ws.Cell(row, c++).Value = VendingReconciliationStatusText(day.Status);
            ws.Cell(row, c++).Value = currency.Currency == "ZIG" ? "ZiG" : currency.Currency;
            ws.Cell(row, c).Value = currency.Sales;
            ws.Cell(row, c++).Style.NumberFormat.Format = FormatCount;
            PutReconciliationMoney(ws.Cell(row, c++), currency.Invoiced, money);
            PutReconciliationMoney(ws.Cell(row, c++), currency.ExpectedCash, money);
            PutReconciliationMoney(ws.Cell(row, c++), currency.OpeningFloat, money);
            PutReconciliationMoney(ws.Cell(row, c++), currency.CountedCash, money);
            PutReconciliationMoney(ws.Cell(row, c++), currency.Accounted, money);
            PutReconciliationMoney(ws.Cell(row, c++), currency.Banked, money);
            PutReconciliationMoney(ws.Cell(row, c++), currency.Variance, money);
            ws.Cell(row, c++).Value = day.VarianceReason ?? string.Empty;
            ws.Cell(row, c++).Value = day.QueryNote ?? string.Empty;
            ws.Cell(row, c++).Value = day.CapturedByName ?? string.Empty;
            ws.Cell(row, c++).Value = day.ApprovedByName ?? string.Empty;
            if (day.ApprovedAt is { } approvedAt)
            {
                ws.Cell(row, c).Value = approvedAt;
                ws.Cell(row, c).Style.NumberFormat.Format = FormatTimestamp;
            }

            if (currency.Variance is < 0m)
            {
                shortRows.Add(row);
            }

            row++;
        }

        row = FinishTable(ws, headerRow, firstDataRow, row, lastCol,
            "No vending cashier sold or was counted in these dates.");

        // Applied after FinishTable, whose stripes would paint over a fill set during the loop.
        foreach (var shortRow in shortRows)
        {
            ws.Cell(shortRow, 15).Style.Font.FontColor = DangerRed;
            ws.Cell(shortRow, 15).Style.Font.Bold = true;
        }

        row = WriteCurrencyTotals(
            ws,
            row,
            lastCol,
            currencyColumn: 7,
            labelColumn: 6,
            lines.GroupBy(line => line.Currency.Currency == "ZIG" ? "ZiG" : line.Currency.Currency),
            (sheet, totalRow, group) =>
            {
                var money = MoneyFormatFor(group.Key);
                var counted = group.Where(line => line.Currency.Variance is not null).ToList();
                sheet.Cell(totalRow, 8).Value = group.Sum(line => line.Currency.Sales);
                sheet.Cell(totalRow, 8).Style.NumberFormat.Format = FormatCount;
                PutReconciliationMoney(sheet.Cell(totalRow, 9), group.Sum(line => line.Currency.Invoiced), money);
                PutReconciliationMoney(sheet.Cell(totalRow, 10), group.Sum(line => line.Currency.ExpectedCash), money);
                PutReconciliationMoney(sheet.Cell(totalRow, 11), counted.Sum(line => line.Currency.OpeningFloat ?? 0m), money);
                PutReconciliationMoney(sheet.Cell(totalRow, 12), counted.Sum(line => line.Currency.CountedCash ?? 0m), money);
                PutReconciliationMoney(sheet.Cell(totalRow, 13), counted.Sum(line => line.Currency.Accounted ?? 0m), money);
                PutReconciliationMoney(sheet.Cell(totalRow, 14), counted.Sum(line => line.Currency.Banked ?? 0m), money);
                PutReconciliationMoney(sheet.Cell(totalRow, 15), counted.Sum(line => line.Currency.Variance ?? 0m), money);
            });

        row = WriteNotice(ws, row + 1, lastCol,
            "Counted, Accounted, Banked and Variance totals are over counted days only; Invoiced and Cash sales are over every day.",
            LightNavy);

        WriteFooter(ws, row - 1, lastCol);
        FinalizeSheet(ws, lastCol, headerRow, landscape: true);
        return WorkbookToBytes(workbook);
    }

    public static string VendingReconciliationStatusText(string status) => status switch
    {
        VendingCashUpStatus.NotCashedUp => "Not cashed up",
        VendingCashUpStatus.Submitted => "Awaiting approval",
        VendingCashUpStatus.Queried => "Queried",
        VendingCashUpStatus.Approved => "Approved",
        _ => status
    };

    /// <summary>A money cell, or a blank one for a figure nobody counted.</summary>
    private static void PutReconciliationMoney(IXLCell cell, decimal? amount, string format)
    {
        if (amount is not { } value)
        {
            return;
        }

        cell.Value = value;
        cell.Style.NumberFormat.Format = format;
    }
}
