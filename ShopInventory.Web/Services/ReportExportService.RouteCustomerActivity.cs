using System.Globalization;
using ClosedXML.Excel;
using ShopInventory.Web.Features.RouteCustomers;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The Route Customers page's activity workbook: every customer classed active, dormant or never bought
/// against one window, with a summary per route, the follow-up lists and a month-by-month history.
/// </summary>
/// <remarks>
/// <para>
/// The classing is <see cref="RouteCustomerActivity"/>, the code the page filters with, so the file and the
/// screen cannot disagree about a shop.
/// </para>
/// <para>
/// Money is one column per currency, never added across them, as in the Route Sales workbook. Coloured cells
/// are coloured after their table is finished: the table's stripes are fills and would paint over them.
/// </para>
/// </remarks>
public partial class ReportExportService
{
    private const string RcaTitle = "Route customer activity";

    /// <summary>Rows measured for column widths on the long sheets; see <see cref="RouteSheetFitRows"/>.</summary>
    private const int RcaFitRows = 300;

    private static readonly XLColor RcaEntryFill = XLColor.FromHtml("#FFFDE7");
    private static readonly XLColor RcaMonthLight = XLColor.FromHtml("#E8EAF6");
    private static readonly XLColor RcaMonthMid = XLColor.FromHtml("#9FA8DA");
    private static readonly XLColor RcaMonthDark = XLColor.FromHtml("#3949AB");

    private sealed record RcaRow(RouteCustomerSalesRowModel Sales, RouteCustomerModel? Detail, RouteCustomerActivityState State);

    private sealed record RcaRouteLine(string Code, int Customers, int Active, int Dormant, int NeverBought, int Sales,
        List<RouteCustomerSalesTotalsModel> Totals);

    public byte[] ExportRouteCustomerActivityToExcel(RouteCustomerActivityReport report)
    {
        using var workbook = NewWorkbook(RcaTitle);

        var rows = report.Customers
            .Select(customer => new RcaRow(
                customer,
                customer.RouteCustomerId is { } id ? report.Details.GetValueOrDefault(id) : null,
                RouteCustomerActivity.Classify(customer, report.WindowDays)))
            .OrderBy(row => row.Sales.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Sales.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Sales.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var currencies = rows
            .SelectMany(row => row.Sales.TotalsByCurrency)
            .Where(total => !string.IsNullOrWhiteSpace(total.Currency))
            .GroupBy(total => total.Currency.Trim().ToUpperInvariant())
            .OrderByDescending(group => group.Sum(total => total.SaleCount))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .ToList();

        var sheets = report.Sheets == RouteCustomerActivitySheets.None ? RouteCustomerActivitySheets.All : report.Sheets;

        if (sheets.HasFlag(RouteCustomerActivitySheets.Summary))
        {
            WriteRcaSummarySheet(AddSheet(workbook, "Summary"), report, rows, currencies);
        }

        if (sheets.HasFlag(RouteCustomerActivitySheets.Customers))
        {
            WriteRcaCustomersSheet(AddSheet(workbook, "Customers", XLColor.FromHtml("#00897B")), report, rows, currencies);
        }

        if (sheets.HasFlag(RouteCustomerActivitySheets.FollowUp))
        {
            WriteRcaFollowUpSheet(AddSheet(workbook, "Follow-up", XLColor.FromHtml("#EF6C00")), report, rows);
        }

        if (sheets.HasFlag(RouteCustomerActivitySheets.NeverBought))
        {
            WriteRcaNeverBoughtSheet(AddSheet(workbook, "Never bought", XLColor.FromHtml("#757575")), report, rows);
        }

        // Only when the history was read: a sheet of blank months would say every shop bought nothing.
        if (sheets.HasFlag(RouteCustomerActivitySheets.ByMonth) && report.Summary.HistoryMonths.Count > 0)
        {
            WriteRcaByMonthSheet(AddSheet(workbook, "By month", RcaMonthDark), report, rows);
        }

        if (workbook.Worksheets.Count == 0)
        {
            WriteRcaSummarySheet(AddSheet(workbook, "Summary"), report, rows, currencies);
        }

        return RcaFixDataBars(WorkbookToBytes(workbook), workbook.Worksheets.Contains("Summary"));
    }

    /// <summary>
    /// The Summary sheet's active-rate bar carries an id ClosedXML writes in lower case, which Excel's schema
    /// refuses (see <see cref="UppercaseDataBarIds"/>). Only that sheet is opened: the others have no bars,
    /// and loading a long Customers sheet costs seconds for no change.
    /// </summary>
    private static byte[] RcaFixDataBars(byte[] workbookBytes, bool hasSummary)
    {
        if (!hasSummary)
        {
            return workbookBytes;
        }

        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, true))
        {
            UppercaseDataBarIds(document, ["Summary"]);
        }

        return stream.ToArray();
    }

    // ── Summary ────────────────────────────────────────────────────────────

    private static void WriteRcaSummarySheet(
        IXLWorksheet ws,
        RouteCustomerActivityReport report,
        IReadOnlyList<RcaRow> rows,
        IReadOnlyList<string> currencies)
    {
        var columns = new List<VpColumn<RcaRouteLine>>
        {
            new("Route", (cell, line) => cell.Value = RsdRouteLabel(report.RouteLabels, line.Code)),
            new("Customers", (cell, line) => VpNumber(cell, line.Customers, FormatCount), FormatCount),
            new("Active", (cell, line) => RsdCount(cell, line.Active, SuccessGreen), FormatCount),
            new("Dormant", (cell, line) => RsdCount(cell, line.Dormant, WarningOrange), FormatCount),
            new("Never bought", (cell, line) => RsdCount(cell, line.NeverBought, DangerRed), FormatCount),
            // A rate does not add up down a column, so it has no total; the card above states the overall one.
            new("Active rate", (cell, line) => VpPercent(cell, line.Customers == 0 ? null : (decimal)line.Active / line.Customers), Bar: true),
            new($"Sales ({report.WindowDays} d)", (cell, line) => VpNumber(cell, line.Sales, FormatCount), FormatCount)
        };

        foreach (var currency in currencies)
        {
            var code = currency;
            columns.Add(new($"Value ({code})",
                (cell, line) => RsdOptionalNumber(cell, RouteSalesOverview.In(line.Totals, code)?.Gross, MoneyFormatFor(code)),
                MoneyFormatFor(code)));
        }

        var lastCol = Math.Max(columns.Count, 5);
        var row = WriteReportHeader(ws, RcaTitle, lastCol, report.Summary.From, report.Summary.To);

        var active = rows.Count(entry => entry.State == RouteCustomerActivityState.Active);
        WriteKpiCard(ws, row, 1, "Customers", rows.Count, FormatCount);
        WriteKpiCard(ws, row, 2, "Active", active, FormatCount, SuccessGreen);
        WriteKpiCard(ws, row, 3, "Dormant", rows.Count(entry => entry.State == RouteCustomerActivityState.Dormant), FormatCount, WarningOrange);
        WriteKpiCard(ws, row, 4, "Never bought", rows.Count(entry => entry.State == RouteCustomerActivityState.NeverBought), FormatCount, DangerRed);
        WriteKpiCard(ws, row, 5, "Active rate", rows.Count == 0 ? 0m : (decimal)active / rows.Count, FormatPercent);
        row += 3;

        RsdNote(ws, row, lastCol,
            $"{report.ScopeDescription}. Active: at least one sale in the {report.WindowDays} days to " +
            $"{report.Summary.To.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}. Dormant: bought before, not in that window. " +
            "Never bought: no sale on record. The last sale is read over all time; sales and value are the window's.");

        var lines = rows
            .GroupBy(entry => entry.Sales.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RcaRouteLine(
                group.Key,
                group.Count(),
                group.Count(entry => entry.State == RouteCustomerActivityState.Active),
                group.Count(entry => entry.State == RouteCustomerActivityState.Dormant),
                group.Count(entry => entry.State == RouteCustomerActivityState.NeverBought),
                group.Sum(entry => entry.Sales.SaleCount),
                group.SelectMany(entry => entry.Sales.TotalsByCurrency).ToList()))
            .OrderBy(line => line.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var table = WriteVpTable(ws, row + 2, columns, lines, "No route customers matched this export.", totals: true);
        WriteFooter(ws, table.NextRow - 1, lastCol);
        FinalizeSheet(ws, lastCol, table.HeaderRow, landscape: true, fitFromRow: table.HeaderRow);
    }

    // ── Customers ──────────────────────────────────────────────────────────

    private static void WriteRcaCustomersSheet(
        IXLWorksheet ws,
        RouteCustomerActivityReport report,
        IReadOnlyList<RcaRow> rows,
        IReadOnlyList<string> currencies)
    {
        var columns = new List<VpColumn<RcaRow>>
        {
            new("Route", (cell, row) => cell.Value = RsdRouteLabel(report.RouteLabels, row.Sales.AssignedBusinessPartnerCode)),
            new("Code", (cell, row) => VpCode(cell, row.Sales.Code)),
            new("Customer", (cell, row) => cell.Value = row.Sales.Name),
            new("Phone", (cell, row) => cell.Value = RcaPhone(row)),
            new("Email", (cell, row) => cell.Value = row.Detail?.Email ?? string.Empty),
            new("Address", (cell, row) => cell.Value = row.Detail?.Address ?? string.Empty),
            new("VAT no.", (cell, row) => cell.Value = row.Detail?.VatNumber ?? string.Empty),
            new("On vans", (cell, row) => cell.Value = RcaOnVans(row)),
            new("Captured", (cell, row) => VpDate(cell, row.Sales.CapturedAt)),
            new("Captured by", (cell, row) => cell.Value = RcaCapturedBy(row)),
            new("First sale", (cell, row) => VpDate(cell, row.Sales.FirstSaleAt)),
            new("Last sale", (cell, row) => VpDate(cell, row.Sales.LastSaleAt)),
            new("Days since", (cell, row) => RsdOptionalNumber(cell, row.Sales.DaysSinceLastSale, FormatCount)),
            new("Activity", (cell, row) => RcaActivity(cell, row.State)),
            new($"Sales ({report.WindowDays} d)", (cell, row) => VpNumber(cell, row.Sales.SaleCount, FormatCount), FormatCount),
            new($"Lines ({report.WindowDays} d)", (cell, row) => VpNumber(cell, row.Sales.LineCount, FormatCount), FormatCount),
            new("Open orders", (cell, row) => VpNumber(cell, row.Sales.OpenOrderCount, FormatCount), FormatCount)
        };

        foreach (var currency in currencies)
        {
            var code = currency;
            columns.Add(new($"Value ({code})",
                (cell, row) => RsdOptionalNumber(cell, RouteSalesOverview.In(row.Sales.TotalsByCurrency, code)?.Gross, MoneyFormatFor(code)),
                MoneyFormatFor(code)));
        }

        var row = WriteReportHeader(ws, "Customers", columns.Count, report.Summary.From, report.Summary.To);
        RsdNote(ws, row, columns.Count,
            $"One line per customer. Activity is read against the {report.WindowDays}-day window; first and last sale are all time. " +
            "Totals follow the filter.");

        // Font-only colouring, so the faster rule-based stripe can be used on what may be thousands of rows.
        var table = WriteVpTable(ws, row + 2, columns, rows, "No route customers matched this export.", totals: true, stripeByRule: true);
        WriteFooter(ws, table.NextRow - 1, columns.Count);
        FinalizeSheet(ws, columns.Count, table.HeaderRow, landscape: true, fitRows: RcaFitRows);
    }

    // ── Follow-up ──────────────────────────────────────────────────────────

    private static void WriteRcaFollowUpSheet(IXLWorksheet ws, RouteCustomerActivityReport report, IReadOnlyList<RcaRow> rows)
    {
        var columns = new List<VpColumn<RcaRow>>
        {
            new("Route", (cell, row) => cell.Value = RsdRouteLabel(report.RouteLabels, row.Sales.AssignedBusinessPartnerCode)),
            new("Code", (cell, row) => VpCode(cell, row.Sales.Code)),
            new("Customer", (cell, row) => cell.Value = row.Sales.Name),
            new("Phone", (cell, row) => cell.Value = RcaPhone(row)),
            new("Address", (cell, row) => cell.Value = row.Detail?.Address ?? string.Empty),
            new("First sale", (cell, row) => VpDate(cell, row.Sales.FirstSaleAt)),
            new("Last sale", (cell, row) => VpDate(cell, row.Sales.LastSaleAt)),
            new("Days since", (cell, row) => RsdCount(cell, row.Sales.DaysSinceLastSale ?? 0, WarningOrange)),
            new("Captured by", (cell, row) => cell.Value = RcaCapturedBy(row)),
            new("Called on", (_, _) => { }),
            new("Outcome", (_, _) => { })
        };

        var dormant = rows
            .Where(row => row.State == RouteCustomerActivityState.Dormant)
            .OrderBy(row => row.Sales.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Sales.DaysSinceLastSale ?? int.MaxValue)
            .ThenBy(row => row.Sales.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var row = WriteReportHeader(ws, "Follow-up", columns.Count, report.Summary.From, report.Summary.To);
        RsdNote(ws, row, columns.Count,
            $"Dormant customers: bought before, nothing in the {report.WindowDays} days to " +
            $"{report.Summary.To.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}. Most recently lapsed first within each route — " +
            "the easiest to win back. Called on and Outcome are for the rep.");

        var table = WriteVpTable(ws, row + 2, columns, dormant, "No customer here is dormant.", totals: false);

        // After the table: its stripes are fills and would cover these.
        if (table.LastRow >= table.FirstRow)
        {
            var entry = ws.Range(table.FirstRow, columns.Count - 1, table.LastRow, columns.Count);
            entry.Style.Fill.BackgroundColor = RcaEntryFill;
            ws.Range(table.FirstRow, columns.Count - 1, table.LastRow, columns.Count - 1).Style.NumberFormat.Format = FormatDate;
        }

        WriteFooter(ws, table.NextRow - 1, columns.Count);
        FinalizeSheet(ws, columns.Count, table.HeaderRow, landscape: true, fitRows: RcaFitRows);
        ws.Column(columns.Count).Width = Math.Max(ws.Column(columns.Count).Width, 36);
    }

    // ── Never bought ───────────────────────────────────────────────────────

    private static void WriteRcaNeverBoughtSheet(IXLWorksheet ws, RouteCustomerActivityReport report, IReadOnlyList<RcaRow> rows)
    {
        var columns = new List<VpColumn<RcaRow>>
        {
            new("Route", (cell, row) => cell.Value = RsdRouteLabel(report.RouteLabels, row.Sales.AssignedBusinessPartnerCode)),
            new("Code", (cell, row) => VpCode(cell, row.Sales.Code)),
            new("Customer", (cell, row) => cell.Value = row.Sales.Name),
            new("Phone", (cell, row) => cell.Value = RcaPhone(row)),
            new("Address", (cell, row) => cell.Value = row.Detail?.Address ?? string.Empty),
            new("On vans", (cell, row) => cell.Value = RcaOnVans(row)),
            new("Captured", (cell, row) => VpDate(cell, row.Sales.CapturedAt)),
            new("Days since captured", (cell, row) => RsdOptionalNumber(cell, RcaDaysSince(row.Sales.CapturedAt, report.Today), FormatCount)),
            new("Captured by", (cell, row) => cell.Value = RcaCapturedBy(row)),
            new("Open orders", (cell, row) => VpNumber(cell, row.Sales.OpenOrderCount, FormatCount), FormatCount)
        };

        var never = rows
            .Where(row => row.State == RouteCustomerActivityState.NeverBought)
            .OrderBy(row => row.Sales.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Sales.CapturedAt ?? DateTime.MaxValue)
            .ThenBy(row => row.Sales.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var row = WriteReportHeader(ws, "Never bought", columns.Count, report.Summary.From, report.Summary.To);
        RsdNote(ws, row, columns.Count,
            "Captured on the road and never sold to, on any date. Oldest capture first within each route.");

        var table = WriteVpTable(ws, row + 2, columns, never, "Every customer here has bought at least once.", totals: true);
        WriteFooter(ws, table.NextRow - 1, columns.Count);
        FinalizeSheet(ws, columns.Count, table.HeaderRow, landscape: true, fitRows: RcaFitRows);
    }

    // ── By month ───────────────────────────────────────────────────────────

    private static void WriteRcaByMonthSheet(IXLWorksheet ws, RouteCustomerActivityReport report, IReadOnlyList<RcaRow> rows)
    {
        var months = report.Summary.HistoryMonths;
        var columns = new List<VpColumn<RcaRow>>
        {
            new("Route", (cell, row) => cell.Value = RsdRouteLabel(report.RouteLabels, row.Sales.AssignedBusinessPartnerCode)),
            new("Code", (cell, row) => VpCode(cell, row.Sales.Code)),
            new("Customer", (cell, row) => cell.Value = row.Sales.Name),
            new("Activity", (cell, row) => RcaActivity(cell, row.State))
        };

        var firstMonthColumn = columns.Count + 1;
        for (var index = 0; index < months.Count; index++)
        {
            var slot = index;
            var month = months[index];
            var header = month.ToString("MMM yyyy", CultureInfo.InvariantCulture);
            if (index == months.Count - 1 && report.Summary.To < month.AddMonths(1).AddDays(-1))
            {
                header += $" (to {report.Summary.To.Day})";
            }

            columns.Add(new(header, (cell, row) => VpNumber(cell, RcaMonth(row, slot), FormatCount), FormatCount));
        }

        columns.Add(new($"{months.Count}-month total",
            (cell, row) => VpNumber(cell, Enumerable.Range(0, months.Count).Sum(slot => RcaMonth(row, slot)), FormatCount),
            FormatCount));

        var row = WriteReportHeader(ws, "By month", columns.Count, months[0], report.Summary.To);
        RsdNote(ws, row, columns.Count,
            "Sales per customer in each calendar month, online and offline together, each on its CAT trading day. " +
            "Shaded by count: the darker, the more often the shop bought.");

        var table = WriteVpTable(ws, row + 2, columns, rows, "No route customers matched this export.", totals: true);

        // After the table, so the stripes do not cover the shading.
        for (var line = table.FirstRow; line <= table.LastRow; line++)
        {
            for (var column = firstMonthColumn; column < firstMonthColumn + months.Count; column++)
            {
                var cell = ws.Cell(line, column);
                var count = cell.GetValue<int>();
                if (count == 0)
                {
                    cell.Style.Font.FontColor = MutedText;
                    continue;
                }

                cell.Style.Fill.BackgroundColor = count <= 2 ? RcaMonthLight : count <= 4 ? RcaMonthMid : RcaMonthDark;
                cell.Style.Font.FontColor = count <= 4 ? NavyBlue : XLColor.White;
                cell.Style.Font.Bold = count > 2;
            }
        }

        WriteFooter(ws, table.NextRow - 1, columns.Count);
        FinalizeSheet(ws, columns.Count, table.HeaderRow, landscape: true, fitRows: RcaFitRows);
    }

    // ── Cells ──────────────────────────────────────────────────────────────

    private static void RcaActivity(IXLCell cell, RouteCustomerActivityState state)
    {
        cell.Value = RouteCustomerActivity.Label(state);
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = state switch
        {
            RouteCustomerActivityState.Active => SuccessGreen,
            RouteCustomerActivityState.Dormant => WarningOrange,
            _ => DangerRed
        };
    }

    private static string RcaPhone(RcaRow row) =>
        !string.IsNullOrWhiteSpace(row.Detail?.Phone) ? row.Detail!.Phone! : row.Sales.Phone ?? string.Empty;

    private static string RcaOnVans(RcaRow row) =>
        row.Sales.IsDeleted ? "Deleted" : row.Sales.IsActive ? "Yes" : "Removed";

    private static string RcaCapturedBy(RcaRow row) =>
        row.Detail is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(row.Detail.CreatedByUserName) ? "System" : row.Detail.CreatedByUserName!;

    private static int RcaMonth(RcaRow row, int slot) =>
        slot < row.Sales.MonthlySaleCounts.Count ? row.Sales.MonthlySaleCounts[slot] : 0;

    private static int? RcaDaysSince(DateTime? date, DateTime today) =>
        date is { } value ? Math.Max(0, (int)(today.Date - value.Date).TotalDays) : null;
}
