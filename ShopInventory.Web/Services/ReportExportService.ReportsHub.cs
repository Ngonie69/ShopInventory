using ClosedXML.Excel;
using ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

namespace ShopInventory.Web.Services;

// The Reports Hub workbook: the hub's page as sheets. Each part the hub could not load is left out
// rather than written as zeros, so a gap in SAP never reads as a quiet day.
public partial class ReportExportService
{
    public byte[] ExportReportsHubToExcel(ReportsHubExport export)
    {
        using var workbook = NewWorkbook("Reports Hub");
        WriteReportsHubOverview(workbook, export);

        var activity = export.Activity;
        if (activity?.DailySales is { } daily)
        {
            WriteReportsHubDailySales(workbook, daily);
        }

        if (activity?.TopCustomers is { } customers)
        {
            WriteReportsHubCustomers(workbook, export.Period, customers);
        }

        if (activity?.TopProducts is { } products)
        {
            WriteReportsHubProducts(workbook, export.Period, products);
        }

        if (activity?.Payments is { } payments)
        {
            WriteReportsHubPayments(workbook, export.Period, payments);
        }

        if (export.Headline?.Aging is { } aging)
        {
            WriteReportsHubAging(workbook, aging);
        }

        return WorkbookToBytes(workbook);
    }

    private static void WriteReportsHubOverview(XLWorkbook workbook, ReportsHubExport export)
    {
        const int cols = 4;
        var period = export.Period;
        var current = export.Headline?.Current;
        var previous = export.Headline?.Previous;
        var aging = export.Headline?.Aging;

        var ws = AddSheet(workbook, "Overview");
        var row = WriteReportHeader(ws, "Reports Hub", cols, Day(period.From), Day(period.To),
            $"Compared with {period.PreviousLabel}");

        if (current is not null)
        {
            WriteKpiCard(ws, row, 1, "Net sales (USD)", current.NetRevenueUSD, FormatUsd);
            WriteKpiCard(ws, row, 2, "Gross profit (USD)", current.GrossProfitUSD, FormatUsd);
            WriteKpiCard(ws, row, 3, "Cash collected (USD)", current.TotalCollectedUSD, FormatUsd);
        }

        if (aging is not null)
        {
            WriteKpiCard(ws, row, 4, "Owed to us now (USD)", aging.TotalOutstandingUSD, FormatUsd, DangerRed);
        }

        row += 3;

        if (current is not null)
        {
            WriteSectionTitle(ws, row, cols, $"This period against {period.PreviousLabel}");
            row++;
            ws.Cell(row, 1).Value = "Measure";
            ws.Cell(row, 2).Value = "This period";
            ws.Cell(row, 3).Value = "Period before";
            ws.Cell(row, 4).Value = "Change";
            StyleTableHeader(ws, row, cols);
            var header = row;
            row++;
            var first = row;

            void Measure(string label, decimal now, decimal? before, string format, bool points = false)
            {
                ws.Cell(row, 1).Value = label;
                ws.Cell(row, 2).Value = now;
                ws.Cell(row, 2).Style.NumberFormat.Format = format;
                if (before is not null)
                {
                    ws.Cell(row, 3).Value = before.Value;
                    ws.Cell(row, 3).Style.NumberFormat.Format = format;
                    if (points)
                    {
                        // A margin's change is in points, not a percentage of a percentage.
                        ws.Cell(row, 4).Value = now - before.Value;
                        ws.Cell(row, 4).Style.NumberFormat.Format = "+0.0\" pt\";-0.0\" pt\";0.0\" pt\"";
                    }
                    else if (before.Value != 0)
                    {
                        ws.Cell(row, 4).Value = (now - before.Value) / before.Value;
                        ws.Cell(row, 4).Style.NumberFormat.Format = "+0.0%;[Red]-0.0%;0.0%";
                    }
                }

                row++;
            }

            Measure("Net sales (USD)", current.NetRevenueUSD, previous?.NetRevenueUSD, FormatUsd);
            Measure("Gross profit (USD)", current.GrossProfitUSD, previous?.GrossProfitUSD, FormatUsd);
            Measure("Gross margin (%)", current.GrossMarginPercent, previous?.GrossMarginPercent, "0.0", points: true);
            Measure("Cash collected (USD)", current.TotalCollectedUSD, previous?.TotalCollectedUSD, FormatUsd);
            Measure("Credit notes (USD)", current.TotalCreditNotesUSD, previous?.TotalCreditNotesUSD, FormatUsd);
            Measure("Invoices", current.TotalInvoices, previous?.TotalInvoices, FormatCount);
            row = FinishTable(ws, header, first, row, cols, filter: false);
            row++;
        }

        WriteSectionTitle(ws, row, cols, "Needs attention");
        row++;
        ws.Cell(row, 1).Value = "Level";
        ws.Cell(row, 2).Value = "What";
        ws.Cell(row, 3).Value = "Detail";
        ws.Cell(row, 4).Value = "Report";
        StyleTableHeader(ws, row, cols);
        var attentionHeader = row;
        row++;
        var attentionFirst = row;
        foreach (var item in export.Attention)
        {
            ws.Cell(row, 1).Value = item.Severity switch
            {
                ReportsAttentionSeverity.Critical => "Critical",
                ReportsAttentionSeverity.Watch => "Watch",
                _ => "FYI"
            };
            ws.Cell(row, 2).Value = item.Title;
            ws.Cell(row, 3).Value = item.Detail;
            ws.Cell(row, 4).Value = item.LinkText;
            row++;
        }

        row = FinishTable(ws, attentionHeader, attentionFirst, row, cols,
            "Nothing needed attention for this period.", filter: false);

        // Fills after FinishTable, which stripes the rows and would paint over them.
        for (var r = attentionFirst; r < attentionFirst + export.Attention.Count; r++)
        {
            var level = ws.Cell(r, 1).GetString();
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 1).Style.Font.FontColor = level == "Critical" ? DangerRed : level == "Watch" ? WarningOrange : NavyBlue;
        }

        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, fitFromRow: attentionHeader);
    }

    private static void WriteReportsHubDailySales(XLWorkbook workbook, Models.SalesSummaryReport daily)
    {
        const int cols = 4;
        var ws = AddSheet(workbook, "Daily sales");
        var row = WriteReportHeader(ws, "Daily sales", cols, daily.FromDate, daily.ToDate);
        ws.Cell(row, 1).Value = "Date";
        ws.Cell(row, 2).Value = "Invoices";
        ws.Cell(row, 3).Value = "Sales (USD)";
        ws.Cell(row, 4).Value = "Sales (ZiG)";
        StyleTableHeader(ws, row, cols);
        var header = row;
        row++;
        var first = row;
        foreach (var day in daily.DailySales.OrderBy(day => day.Date))
        {
            ws.Cell(row, 1).Value = day.Date;
            ws.Cell(row, 1).Style.NumberFormat.Format = FormatDayDate;
            ws.Cell(row, 2).Value = day.InvoiceCount;
            ws.Cell(row, 2).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 3).Value = day.TotalSalesUSD;
            ws.Cell(row, 3).Style.NumberFormat.Format = FormatUsd;
            ws.Cell(row, 4).Value = day.TotalSalesZIG;
            ws.Cell(row, 4).Style.NumberFormat.Format = FormatZig;
            row++;
        }

        var last = row - 1;
        row = FinishTable(ws, header, first, row, cols, "No invoices were raised in this range.");
        if (last >= first)
        {
            ws.Cell(row, 1).Value = "TOTAL";
            WriteSubtotal(ws, row, 2, first, last, FormatCount);
            WriteSubtotal(ws, row, 3, first, last, FormatUsd);
            WriteSubtotal(ws, row, 4, first, last, FormatZig);
            StyleTotalsRow(ws, row, cols);
        }

        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, header);
    }

    private static void WriteReportsHubCustomers(XLWorkbook workbook, ReportsPeriod period, Models.TopCustomersReport report)
    {
        const int cols = 6;
        var ws = AddSheet(workbook, "Top customers");
        var row = WriteReportHeader(ws, "Top customers", cols, Day(period.From), Day(period.To));
        ws.Cell(row, 1).Value = "Code";
        ws.Cell(row, 2).Value = "Customer";
        ws.Cell(row, 3).Value = "Invoices";
        ws.Cell(row, 4).Value = "Sales (USD)";
        ws.Cell(row, 5).Value = "Margin";
        ws.Cell(row, 6).Value = "Balance (USD)";
        StyleTableHeader(ws, row, cols);
        var header = row;
        row++;
        var first = row;
        foreach (var customer in report.TopCustomers.OrderBy(customer => customer.Rank))
        {
            ws.Cell(row, 1).Value = customer.CardCode;
            ws.Cell(row, 2).Value = customer.CardName;
            ws.Cell(row, 3).Value = customer.InvoiceCount;
            ws.Cell(row, 3).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 4).Value = customer.TotalPurchasesUSD;
            ws.Cell(row, 4).Style.NumberFormat.Format = FormatUsd;
            if (customer.GrossMarginPercent is { } margin)
            {
                ws.Cell(row, 5).Value = margin / 100m;
                ws.Cell(row, 5).Style.NumberFormat.Format = FormatPercent;
            }

            ws.Cell(row, 6).Value = customer.OutstandingBalanceUSD;
            ws.Cell(row, 6).Style.NumberFormat.Format = FormatUsd;
            row++;
        }

        row = FinishTable(ws, header, first, row, cols, "No customer bought in this period.");
        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, header);
    }

    private static void WriteReportsHubProducts(XLWorkbook workbook, ReportsPeriod period, Models.TopProductsReport report)
    {
        const int cols = 5;
        var ws = AddSheet(workbook, "Top products");
        var row = WriteReportHeader(ws, "Top products", cols, Day(period.From), Day(period.To));
        ws.Cell(row, 1).Value = "Code";
        ws.Cell(row, 2).Value = "Product";
        ws.Cell(row, 3).Value = "Units";
        ws.Cell(row, 4).Value = "Sales (USD)";
        ws.Cell(row, 5).Value = "Margin";
        StyleTableHeader(ws, row, cols);
        var header = row;
        row++;
        var first = row;
        foreach (var product in report.TopProducts.OrderBy(product => product.Rank))
        {
            ws.Cell(row, 1).Value = product.ItemCode;
            ws.Cell(row, 2).Value = product.ItemName;
            ws.Cell(row, 3).Value = product.TotalQuantitySold;
            ws.Cell(row, 3).Style.NumberFormat.Format = FormatQuantity;
            ws.Cell(row, 4).Value = product.TotalRevenueUSD;
            ws.Cell(row, 4).Style.NumberFormat.Format = FormatUsd;
            if (product.GrossMarginPercent is { } margin)
            {
                ws.Cell(row, 5).Value = margin / 100m;
                ws.Cell(row, 5).Style.NumberFormat.Format = FormatPercent;
            }

            row++;
        }

        row = FinishTable(ws, header, first, row, cols, "Nothing was sold in this period.");
        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, header);
    }

    private static void WriteReportsHubPayments(XLWorkbook workbook, ReportsPeriod period, Models.PaymentSummaryReport report)
    {
        const int cols = 5;
        var ws = AddSheet(workbook, "Payments");
        var row = WriteReportHeader(ws, "How customers paid", cols, Day(period.From), Day(period.To));
        ws.Cell(row, 1).Value = "Method";
        ws.Cell(row, 2).Value = "Payments";
        ws.Cell(row, 3).Value = "Amount (USD)";
        ws.Cell(row, 4).Value = "Amount (ZiG)";
        ws.Cell(row, 5).Value = "Share";
        StyleTableHeader(ws, row, cols);
        var header = row;
        row++;
        var first = row;
        foreach (var method in report.PaymentsByMethod.OrderByDescending(method => method.TotalAmountUSD))
        {
            ws.Cell(row, 1).Value = method.PaymentMethod;
            ws.Cell(row, 2).Value = method.PaymentCount;
            ws.Cell(row, 2).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 3).Value = method.TotalAmountUSD;
            ws.Cell(row, 3).Style.NumberFormat.Format = FormatUsd;
            ws.Cell(row, 4).Value = method.TotalAmountZIG;
            ws.Cell(row, 4).Style.NumberFormat.Format = FormatZig;
            ws.Cell(row, 5).Value = method.PercentageOfTotal / 100m;
            ws.Cell(row, 5).Style.NumberFormat.Format = FormatPercent;
            row++;
        }

        row = FinishTable(ws, header, first, row, cols, "No payments were received in this period.");
        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, header);
    }

    private static void WriteReportsHubAging(XLWorkbook workbook, Models.ReceivablesAgingReport aging)
    {
        const int cols = 4;
        var ws = AddSheet(workbook, "Owed by age");
        var row = WriteReportHeader(ws, "Owed to us, by age", cols, subtitle: $"As at {aging.ReportDate:dd MMM yyyy}");
        ws.Cell(row, 1).Value = "Age";
        ws.Cell(row, 2).Value = "Invoices";
        ws.Cell(row, 3).Value = "Amount (USD)";
        ws.Cell(row, 4).Value = "Share";
        StyleTableHeader(ws, row, cols);
        var header = row;
        row++;
        var first = row;
        foreach (var (label, bucket) in ReportsHubAgingBuckets(aging))
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 2).Value = bucket.InvoiceCount;
            ws.Cell(row, 2).Style.NumberFormat.Format = FormatCount;
            ws.Cell(row, 3).Value = bucket.AmountUSD;
            ws.Cell(row, 3).Style.NumberFormat.Format = FormatUsd;
            ws.Cell(row, 4).Value = bucket.PercentOfTotal / 100m;
            ws.Cell(row, 4).Style.NumberFormat.Format = FormatPercent;
            row++;
        }

        var last = row - 1;
        row = FinishTable(ws, header, first, row, cols, filter: false);
        ws.Cell(row, 1).Value = "TOTAL";
        WriteSubtotal(ws, row, 2, first, last, FormatCount);
        WriteSubtotal(ws, row, 3, first, last, FormatUsd);
        StyleTotalsRow(ws, row, cols);
        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, header);
    }

    /// <summary>The aging buckets in age order, named the way the hub names them.</summary>
    public static IEnumerable<(string Label, Models.AgingBucket Bucket)> ReportsHubAgingBuckets(Models.ReceivablesAgingReport aging) =>
    [
        ("0–30 days", aging.Current),
        ("31–60 days", aging.Days31To60),
        ("61–90 days", aging.Days61To90),
        ("Over 90 days", aging.Over90Days),
    ];

    private static DateTime Day(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);
}
