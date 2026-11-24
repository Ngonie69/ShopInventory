using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using ShopInventory.Web.Models;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace ShopInventory.Web.Services;

/// <summary>
/// The van sales management report as a workbook, laid out as the office's "Vansales Daily Report"
/// workbook was: Insights Summary, Management Overview, Monthly Summary, Daily Management View, Daily
/// Truck Cards, then PCR &amp; CCR Trends (not in the office's book), Weekly Sales, Route Sales Ranking, the product group, Breakages and Raw Data.
/// </summary>
/// <remarks>
/// <para>
/// Every figure is the report the page shows, built once by <see cref="VanManagementReportBuilder"/>.
/// Monthly Summary and Raw Data are the Daily Truck Report's own two sheets, so the two workbooks agree
/// to the cent. Money is a number, a blank is "not recorded", and a rate with nothing under it is an em
/// dash, as on that workbook.
/// </para>
/// <para>
/// The charts are native Excel charts, added with the OpenXML SDK once ClosedXML has saved the book, and
/// every one reads a hidden "Chart Data" sheet, so a reader can restyle or re-point them. Each is anchored
/// at a fixed size in pixels from its top-left cell, because the sheets' columns are fitted to their
/// content and a chart stretched between two cells would change size from sheet to sheet.
/// </para>
/// </remarks>
public partial class ReportExportService
{
    /// <summary>One column of a management table: its heading, its value for a row, and how to show it.</summary>
    private sealed record MgmtColumn<TRow>(string Header, Func<TRow, object?> Value, string? Format = null);

    private const string MgmtChartDataSheet = "Chart Data";

    // The palette of the design: navy, header blue, a pale track, orange for targets only.
    private const string MgmtNavyHex = "1B3A5C";
    private const string MgmtBlueHex = "2C5F8A";
    private const string MgmtSoftBlueHex = "7FA6CC";
    private const string MgmtTrackHex = "DCE6F0";
    private const string MgmtOrangeHex = "C45A12";
    private const string MgmtPeachHex = "F2C7A2";
    private static readonly string[] MgmtItemHexes = ["1B3A5C", "4A7DAA", "9DB8D3", "C45A12", "F2C7A2", "5A6A7A"];
    private static readonly string[] MgmtTenderHexes = ["2C5F8A", "C45A12", "9DB8D3", "F2C7A2", "5A6A7A"];

    private const int MgmtChartWidth = 560;
    private const int MgmtChartGap = 20;
    private const int MgmtChartHeight = 340;
    private const int MgmtRowPixels = 20;

    private enum MgmtChartType { Column, Bar, Line, Doughnut }

    /// <summary>A block of figures on the Chart Data sheet: a heading row, then a row per category.</summary>
    private sealed record MgmtBlock(int HeaderRow, int FirstRow, int LastRow)
    {
        public bool HasRows => LastRow >= FirstRow;
    }

    /// <summary>One series: the Chart Data column it reads, its colour, and whether it is drawn as a line.</summary>
    /// <remarks><c>MarkersOnly</c> draws a line series as a dash at each category and no line between them.</remarks>
    private sealed record MgmtSeries(int Column, string Color, bool Line = false, bool Dashed = false, bool MarkersOnly = false);

    private sealed record MgmtChartSpec(
        string Title,
        MgmtBlock Block,
        IReadOnlyList<MgmtSeries> Series,
        MgmtChartType Type,
        bool Stacked = false,
        bool Overlap = false,
        bool Labels = false,
        string AxisFormat = "$#,##0",
        bool Legend = true);

    /// <summary>Where a chart goes: its sheet, the row its top sits on, and its left edge and size in pixels.</summary>
    private sealed record MgmtPlacement(string Sheet, int Row, int Left, int Width, int Height, MgmtChartSpec Spec);

    /// <summary>The Chart Data sheet as it is written, and the charts waiting for it.</summary>
    private sealed class MgmtCharts(IXLWorksheet data)
    {
        private int _next = 1;

        public List<MgmtPlacement> Placements { get; } = [];

        /// <summary>Writes a block: a heading per column, then one row per item. Null leaves the cell empty.</summary>
        public MgmtBlock Block<T>(string title, string[] headers, IEnumerable<T> items, Func<T, object?[]> values)
        {
            data.Cell(_next, 1).Value = title;
            data.Cell(_next, 1).Style.Font.Bold = true;
            int header = _next + 1;

            for (int col = 0; col < headers.Length; col++)
            {
                data.Cell(header, col + 1).Value = headers[col];
                data.Cell(header, col + 1).Style.Font.Bold = true;
            }

            int row = header + 1;
            foreach (var item in items)
            {
                var cells = values(item);
                for (int col = 0; col < cells.Length; col++)
                {
                    MgmtCell(data.Cell(row, col + 1), cells[col], null);
                }

                row++;
            }

            _next = row + 1;
            return new MgmtBlock(header, header + 1, row - 1);
        }

        /// <summary>Queues a chart on a sheet; nothing is queued over an empty block.</summary>
        public void Place(string sheet, int row, int slot, MgmtChartSpec spec, int width = MgmtChartWidth, int height = MgmtChartHeight)
        {
            if (spec.Block.HasRows)
            {
                Placements.Add(new MgmtPlacement(sheet, row, slot * (MgmtChartWidth + MgmtChartGap), width, height, spec));
            }
        }
    }

    public byte[] ExportVanManagementReportToExcel(VanManagementReport report, DateTime day, DateTime? weekStart)
    {
        byte[] bytes;
        List<MgmtPlacement> placements;

        using (var workbook = NewWorkbook("Van Sales Management Report"))
        {
            var now = DateTime.UtcNow.AddHours(2); // CAT
            var data = workbook.Worksheets.Add(MgmtChartDataSheet);
            var charts = new MgmtCharts(data);

            BuildMgmtInsightsSheet(workbook, report, charts, now);
            BuildMgmtOverviewSheet(workbook, report, charts, now);

            BuildVanDailyTruckSummarySheet(workbook, report.Truck, now);
            workbook.Worksheet("Trucks").Name = "Monthly Summary";

            BuildMgmtDailySheet(workbook, report, day, charts, now);
            BuildMgmtTruckCardsSheet(workbook, report, day, now);
            BuildMgmtCallRatesSheet(workbook, report, charts, now);
            BuildMgmtWeeklySheet(workbook, report, weekStart, charts, now);
            BuildMgmtRoutesSheet(workbook, report, charts, now);

            if (report.ProductGroupName is not null)
            {
                BuildMgmtProductsSheet(workbook, report, charts, now);
            }

            BuildMgmtBreakagesSheet(workbook, report, charts, now);

            BuildVanDailyTruckDaysSheet(workbook, report.Truck, now);
            workbook.Worksheet("Days").Name = "Raw Data";

            // The figures the charts read sit last and out of sight; unhiding it shows every one.
            data.Position = workbook.Worksheets.Count;
            data.Columns().AdjustToContents();
            data.TabSelected = false;
            data.Hide();
            workbook.Worksheet(1).SetTabActive();
            workbook.Worksheet(1).TabSelected = true;

            // Columns are final now, so a chart's left edge can be turned into a cell and an offset.
            placements = charts.Placements;
            var anchors = placements.ToDictionary(placement => placement, placement => MgmtColumnAt(workbook.Worksheet(placement.Sheet), placement.Left));

            bytes = WorkbookToBytes(workbook);
            return AddMgmtCharts(bytes, placements, anchors);
        }
    }

    // — Insights Summary ————————————————————————————————————————————————————————————————————————

    private static void BuildMgmtInsightsSheet(XLWorkbook workbook, VanManagementReport report, MgmtCharts charts, DateTime now)
    {
        const int lastCol = 7;
        const string sheet = "Insights Summary";
        var ws = workbook.Worksheets.Add(sheet);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"{MgmtPeriod(report).ToUpperInvariant()}  |  SALES PERFORMANCE INSIGHTS", lastCol, now);
        row = MgmtNote(ws, row, lastCol,
            $"Reporting through {report.ReportDate:d MMMM yyyy}  ·  {report.Trucks.Count} trucks  ·  {report.Routes.SalesRecords} truck-days reported  ·  {report.Total.SalesCount:N0} invoices. " +
            "Van sales include Ecocash and exclude account customers, which are reported separately.");
        row++;

        var total = report.Total;
        var pace = report.Truck.WorkingDays > 0 ? (double)report.WorkingDaysToReportDate / report.Truck.WorkingDays : (double?)null;
        row = MgmtKpiStrip(ws, row, lastCol,
            ("Van Sales To Date", total.Sales, MgmtMoney, null),
            ("Target Achieved", total.TargetAchieved, MgmtRate, MgmtToneInk(total.TargetAchieved)),
            ("Pace By Today", pace, MgmtRate, null),
            ("Gap To Target", total.BalanceToTarget, MgmtMoney, null),
            ("Daily Run-Rate", report.DailyRunRate, MgmtMoney, null),
            ("Needed Per Day Left", report.RequiredPerRemainingDay, MgmtMoney, TsOrange),
            ("Customers", total.CustomersVisited, MgmtWhole, null));

        // Month pace: cumulative sales against the fleet's target day by day.
        var fleetDaily = total.DailySalesTarget ?? 0m;
        decimal running = 0m, paceRunning = 0m;
        var paceRows = report.Dates.Select(date =>
        {
            running += report.Truck.Days.Where(d => d.TradingDate.Date == date).Sum(d => d.Sales);
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                paceRunning += fleetDaily;
            }

            return (Label: date.ToString("d MMM"), Actual: date <= report.ReportDate ? running : (decimal?)null, Pace: fleetDaily > 0 ? paceRunning : (decimal?)null);
        }).ToList();

        var paceBlock = charts.Block("Month pace", ["Day", "Cumulative van sales", "Target pace"], paceRows,
            item => [item.Label, item.Actual, item.Pace]);
        var truckBlock = MgmtTruckBlock(charts, report);

        charts.Place(sheet, row, 0, new MgmtChartSpec(
            "Month pace — cumulative van sales against target", paceBlock,
            [new MgmtSeries(2, MgmtBlueHex, Line: true), new MgmtSeries(3, MgmtOrangeHex, Line: true, Dashed: true)],
            MgmtChartType.Line));
        charts.Place(sheet, row, 1, new MgmtChartSpec(
            "Van sales against month target, by truck", truckBlock,
            [new MgmtSeries(2, MgmtTrackHex), new MgmtSeries(3, MgmtBlueHex)],
            MgmtChartType.Bar, Overlap: true));
        row = MgmtChartBand(row);

        TsSectionTitle(ws, row, lastCol, "FINDINGS");
        row += 2;
        int index = 1;
        foreach (var insight in report.Insights)
        {
            row = MgmtFinding(ws, row, lastCol, index++, insight);
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "TERRITORY PERFORMANCE  |  RANKED BY SALES");
        row += 2;
        int firstData = row + 1;
        row = MgmtTable(ws, row, lastCol, report.Ranked.Select((truck, rank) => (truck, rank)).ToList(),
        [
            new("#", item => item.rank + 1, FormatCount),
            new("Truck", item => item.truck.RouteName),
            new("Van Sales", item => item.truck.Sales, FormatUsd),
            new("Target Achieved", item => item.truck.TargetAchieved, FormatPercent),
            new("Sales / Km", item => item.truck.SalesPerKilometre, FormatUsd),
            new("PCR", item => item.truck.ProductiveCallRate, FormatPercent),
            new("Average Order", item => item.truck.AverageOrderValue, FormatUsd)
        ], total: ["", "ALL TRUCKS", total.Sales, total.TargetAchieved, total.SalesPerKilometre, total.ProductiveCallRate, total.AverageOrderValue]);

        if (report.Ranked.Count > 0)
        {
            int lastData = firstData + report.Ranked.Count - 1;
            foreach (var (truck, offset) in report.Ranked.Select((truck, offset) => (truck, offset)))
            {
                ws.Cell(firstData + offset, 4).Style.Font.FontColor = MgmtToneInk(truck.TargetAchieved) ?? TsTextDark;
                ws.Cell(firstData + offset, 4).Style.Font.Bold = true;
            }

            // A bar in each cell, measured against the whole target, not against the best truck.
            ws.Range(firstData, 4, lastData, 4)
                .AddConditionalFormat()
                .DataBar(XLColor.FromHtml("#" + MgmtSoftBlueHex))
                .Minimum(XLCFContentType.Number, "0")
                .Maximum(XLCFContentType.Number, "1");
        }

        row = MgmtNote(ws, row - 1, lastCol, "Target achieved: a bar against the whole month's target, and the figure in red under 35%, amber under 50%, navy from 50% and green at 100%.");
        row++;

        TsSectionTitle(ws, row, lastCol, "ROUTE SALES  |  HIGHEST · MIDDLE · LOWEST");
        row += 2;
        var positions = new List<(string Position, VanManagementRouteSales? Route)>
        {
            ("HIGHEST", report.Routes.Highest), ("MIDDLE", report.Routes.Middle), ("LOWEST", report.Routes.Lowest)
        };
        row = MgmtTable(ws, row, lastCol, positions.Where(item => item.Route is not null).ToList(),
        [
            new("Position", item => item.Position),
            new("Area Worked", item => item.Route!.Area),
            new("Van Sales", item => item.Route!.Sales, FormatUsd),
            new("Days", item => item.Route!.Days, FormatCount)
        ]);

        TsSectionTitle(ws, row, lastCol, report.OperatingIssues.Count == 0
            ? "OPERATING ISSUES  |  NONE RECORDED"
            : $"OPERATING ISSUES  |  {report.OperatingIssues.Count} FLAGGED TRUCK-DAYS — SEE MANAGEMENT OVERVIEW");

        TsFinalize(ws, lastCol);
        ws.Column(1).Width = 12;
        ws.Column(2).Width = 34;
    }

    // — Management Overview —————————————————————————————————————————————————————————————————————

    private static void BuildMgmtOverviewSheet(XLWorkbook workbook, VanManagementReport report, MgmtCharts charts, DateTime now)
    {
        const int lastCol = 7;
        const string sheet = "Management Overview";
        var ws = workbook.Worksheets.Add(sheet);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"VAN SALES  |  MANAGEMENT OVERVIEW  —  {MgmtPeriod(report).ToUpperInvariant()}", lastCol, now);
        var total = report.Total;
        var payments = report.Payments;

        row = MgmtKpiStrip(ws, row, lastCol,
            ("Month Target With VAT", total.PeriodTarget, MgmtMoney, null),
            ("Month Target Before VAT", VanManagementReport.BeforeVat(total.PeriodTarget), MgmtMoney, null),
            ("Van Sales To Date", total.Sales, MgmtMoney, null),
            ("Balance To Target", total.BalanceToTarget, MgmtMoney, null),
            ("Target Achieved", total.TargetAchieved, MgmtRate, MgmtToneInk(total.TargetAchieved)),
            ("Km A Litre", total.KilometresPerLitre, "0.00", null),
            ("Fuel Issued (L)", total.FuelLitres > 0 ? total.FuelLitres : null, MgmtWhole, null));

        var truckBlock = MgmtTruckBlock(charts, report);
        var tenders = new List<(string Tender, decimal Amount)>
        {
            ("Cash", payments.Cash), ("Ecocash", payments.Ecocash), ("InnBucks", payments.Innbucks),
            ("Card, transfer or other", payments.OtherTender), ("No tender recorded", payments.Untendered)
        }.Where(tender => tender.Amount > 0).ToList();
        var tenderBlock = charts.Block("Van sales by tender", ["Tender", "Van sales"], tenders, item => [item.Tender, item.Amount]);

        charts.Place(sheet, row, 0, new MgmtChartSpec(
            "Sold and still to sell, by truck", truckBlock,
            [new MgmtSeries(3, MgmtBlueHex), new MgmtSeries(4, MgmtPeachHex)],
            MgmtChartType.Bar, Stacked: true));
        charts.Place(sheet, row, 1, new MgmtChartSpec(
            "Van sales by tender (Ecocash is part of van sales)", tenderBlock,
            [new MgmtSeries(2, MgmtBlueHex)], MgmtChartType.Doughnut));
        row = MgmtChartBand(row);

        TsSectionTitle(ws, row, lastCol, "EACH TRUCK AGAINST ITS TARGET");
        row += 2;
        int firstData = row + 1;
        row = MgmtTable(ws, row, lastCol, report.Trucks.ToList(),
        [
            new("Truck", truck => truck.RouteName),
            new("Target With VAT", truck => truck.PeriodTarget, FormatUsd),
            new("Target Before VAT", truck => VanManagementReport.BeforeVat(truck.PeriodTarget), FormatUsd),
            new("Van Sales To Date", truck => truck.Sales, FormatUsd),
            new("Balance", truck => truck.BalanceToTarget, FormatUsdSigned),
            new("% Target", truck => truck.TargetAchieved, FormatPercent),
            new("Days Reported", truck => $"{truck.DaysReported} of {truck.WorkingDaysElapsed}")
        ], total: ["ALL TRUCKS", total.PeriodTarget, VanManagementReport.BeforeVat(total.PeriodTarget), total.Sales, total.BalanceToTarget, total.TargetAchieved, $"{total.DaysReported} of {total.WorkingDaysElapsed}"]);

        foreach (var (truck, offset) in report.Trucks.Select((truck, offset) => (truck, offset)))
        {
            MgmtTone(ws.Cell(firstData + offset, 6), truck.TargetAchieved);

            if (truck.DaysReported < truck.WorkingDaysElapsed)
            {
                ws.Cell(firstData + offset, 7).Style.Font.FontColor = TsOrange;
                ws.Cell(firstData + offset, 7).Style.Font.Bold = true;
            }
        }

        row = MgmtNote(ws, row - 1, lastCol,
            $"Targets are set with VAT, as van sales are counted. Before VAT is the target taken back at the standard {VanManagementReport.TargetVatRateText} rate; balance and % of target are against the target with VAT.");
        row++;

        TsSectionTitle(ws, row, lastCol, "MONTH-TO-DATE VANSALES PAYMENT SUMMARY");
        row += 2;
        row = MgmtNote(ws, row, lastCol, "Van sales include Ecocash. Cash sales are van sales less Ecocash; account deliveries are their own figure and not in van sales.");
        var paymentRows = new List<(string Label, object? Value, string Format, bool Strong)>
        {
            ("Vansales total sales", payments.VanSales, FormatUsd, true),
            ("Ecocash for the period", payments.Ecocash, FormatUsd, false),
            ("Vansales cash sales (total less Ecocash)", payments.LessEcocash, FormatUsd, true),
            ("   of which recorded as cash", payments.Cash, FormatUsd, false),
            ("   InnBucks", payments.Innbucks, FormatUsd, false),
            ("   Card, transfer or other", payments.OtherTender, FormatUsd, false),
            ("   No tender recorded", payments.Untendered, FormatUsd, false),
            ("Account customer deliveries by vansales", payments.AccountSales, FormatUsd, true),
            ("Accounts / van sales", payments.AccountsShare, FormatPercent, false)
        };
        int paymentsFirst = row + 1;
        row = MgmtTable(ws, row, lastCol, paymentRows,
        [
            new("Figure", item => item.Label),
            new("Amount", item => item.Value, null)
        ], formatOf: (item, col) => col == 2 ? item.Format : null);

        foreach (var (item, offset) in paymentRows.Select((item, offset) => (item, offset)))
        {
            if (item.Strong)
            {
                ws.Range(paymentsFirst + offset, 1, paymentsFirst + offset, 2).Style.Font.Bold = true;
            }
            else
            {
                ws.Range(paymentsFirst + offset, 1, paymentsFirst + offset, 2).Style.Font.FontColor = TsTextMuted;
            }
        }

        TsSectionTitle(ws, row, lastCol, "ACCOUNT CUSTOMER SALES  |  SEPARATE FROM VAN SALES");
        row += 2;
        row = MgmtTable(ws, row, lastCol, report.Trucks.ToList(),
        [
            new("Truck", truck => truck.RouteName),
            new("Account Sales (USD)", truck => truck.AccountSales > 0 ? truck.AccountSales : "—", FormatUsd),
            new("Van Sales (USD)", truck => truck.Sales, FormatUsd),
            new("Accounts / Van Sales", truck => truck.Sales > 0 && truck.AccountSales > 0 ? (double)(truck.AccountSales / truck.Sales) : null, FormatPercent)
        ], total: ["ALL TRUCKS", total.AccountSales, total.Sales, payments.AccountsShare]);

        TsSectionTitle(ws, row, lastCol, "AUTOMATIC MANAGEMENT EXCEPTION MONITOR");
        row += 2;
        int checksFirst = row + 1;
        row = MgmtTable(ws, row, lastCol, report.ExceptionChecks.ToList(),
        [
            new("Rule", check => check.Rule),
            new("Truck-Days", check => check.Count, FormatCount),
            new("Action", check => check.Count > 0 ? "REVIEW" : "CLEAR"),
            new("What It Means", check => check.Meaning)
        ]);

        foreach (var (check, offset) in report.ExceptionChecks.Select((check, offset) => (check, offset)))
        {
            var action = ws.Cell(checksFirst + offset, 3);
            action.Style.Font.Bold = true;
            action.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            action.Style.Fill.BackgroundColor = XLColor.FromHtml(check.Count > 0 ? "#FBE7D3" : "#E3F1E4");
            action.Style.Font.FontColor = XLColor.FromHtml(check.Count > 0 ? "#8A3F0B" : "#1E5E22");

            if (check.Count > 0)
            {
                ws.Cell(checksFirst + offset, 2).Style.Font.FontColor = TsOrange;
                ws.Cell(checksFirst + offset, 2).Style.Font.Bold = true;
            }
        }

        TsSectionTitle(ws, row, lastCol, "OPERATING ISSUES LOG  |  ENTERED AS A NOTE ON THE TRUCK'S DAY");
        row += 2;
        if (report.OperatingIssues.Count == 0)
        {
            MgmtNote(ws, row, lastCol, "None recorded in the period. A note on a truck's day in the Daily Truck Report appears here.");
        }
        else
        {
            MgmtTable(ws, row, lastCol, report.OperatingIssues.ToList(),
            [
                new("Date", day => day.TradingDate.Date, FormatDayDate),
                new("Truck", day => day.RouteName),
                new("Operating Issue", day => day.Notes),
                new("Sales Entry", day => day.HasActivity ? day.Sales : "No sale reported", FormatUsd)
            ]);
        }

        TsFinalize(ws, lastCol);
        ws.Column(1).Width = 40;
    }

    // — Daily Management View and Truck Cards ———————————————————————————————————————————————————

    private static void BuildMgmtDailySheet(XLWorkbook workbook, VanManagementReport report, DateTime day, MgmtCharts charts, DateTime now)
    {
        const int lastCol = 8;
        const string sheet = "Daily Management View";
        var ws = workbook.Worksheets.Add(sheet);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"DAILY MANAGEMENT  |  {day.ToString("dddd d MMMM yyyy").ToUpperInvariant()}", lastCol, now);
        var rows = VanManagementReportBuilder.DayView(report, day);
        var reporting = rows.Count(item => item.Day?.HasActivity == true);
        var daySales = rows.Sum(item => item.Day?.Sales ?? 0m);
        var monthToDate = rows.Sum(item => item.ToDate.Sales);
        var top = rows.Where(item => item.Day is { Sales: > 0 }).OrderByDescending(item => item.Day!.Sales).FirstOrDefault();

        row = MgmtKpiStrip(ws, row, lastCol,
            ("Daily Van Sales", daySales, MgmtMoney, null),
            ("Trucks Reporting", $"{reporting} / {rows.Count}", null, reporting < rows.Count ? TsOrange : null),
            ("MTD Van Sales", monthToDate, MgmtMoney, null),
            ("Customers", rows.Sum(item => item.Day?.CustomersVisited ?? 0), MgmtWhole, null),
            ("Km Today", rows.Sum(item => item.Day?.Kilometres ?? 0), MgmtWhole, null),
            ("Top Truck", top?.Truck.RouteName ?? "—", null, null));

        var dayBlock = charts.Block($"Trucks on {day:d MMM}", ["Truck", "Day sales", "Daily target"], rows,
            item => [item.Truck.RouteName, item.Day?.HasActivity == true ? item.Day.Sales : null, item.Truck.DailySalesTarget]);
        var fleetDays = report.Dates.Where(date => date <= new[] { day, report.ReportDate }.Max()).ToList();
        var fleetBlock = charts.Block("Fleet day by day", ["Day", "Van sales", "Fleet daily target"], fleetDays, date =>
        {
            var onDay = report.Truck.Days.Where(d => d.TradingDate.Date == date).ToList();
            var working = date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            return [date.ToString("d MMM"), onDay.Any(d => d.HasActivity) ? onDay.Sum(d => d.Sales) : null, working ? report.Total.DailySalesTarget : null];
        });

        // Full width, one above the other: the trucks' SAP names are long, and squeezed into half a sheet
        // they wrap into towers under the columns. Each truck's own target is a mark on its column, not a
        // line, because a line between trucks would draw a trend that does not exist.
        const int wide = MgmtChartWidth * 2 + MgmtChartGap;
        charts.Place(sheet, row, 0, new MgmtChartSpec(
            "Van sales by truck, against the daily target", dayBlock,
            [new MgmtSeries(2, MgmtBlueHex), new MgmtSeries(3, MgmtOrangeHex, Line: true, MarkersOnly: true)],
            MgmtChartType.Column), width: wide);
        row = MgmtChartBand(row);
        charts.Place(sheet, row, 0, new MgmtChartSpec(
            "Fleet van sales, day by day", fleetBlock,
            [new MgmtSeries(2, MgmtSoftBlueHex), new MgmtSeries(3, MgmtOrangeHex, Line: true, Dashed: true)],
            MgmtChartType.Column), width: wide);
        row = MgmtChartBand(row);

        TsSectionTitle(ws, row, lastCol, "EVERY TRUCK ON THE DAY");
        row += 2;
        int firstData = row + 1;
        row = MgmtTable(ws, row, lastCol, rows.ToList(),
        [
            new("Truck", item => item.Truck.RouteName),
            new("Area Worked", item => item.Day?.AreaName),
            new("Daily Sales", item => item.Day?.HasActivity == true ? item.Day.Sales : "not reported", FormatUsd),
            new("MTD Sales", item => item.ToDate.Sales, FormatUsd),
            new("Target Met", item => item.ToDate.TargetAchieved, FormatPercent),
            new("Customers", item => item.Day?.HasActivity == true ? item.Day.CustomersVisited : null, FormatCount),
            new("Km Today", item => item.Day?.Kilometres, FormatCount),
            new("Account Sales*", item => item.Day?.AccountSales ?? (object)"—", FormatUsd)
        ], total:
        [
            "TOTAL", null, daySales, monthToDate,
            report.Total.PeriodTarget is > 0 ? (double)(monthToDate / report.Total.PeriodTarget.Value) : null,
            rows.Sum(item => item.Day?.CustomersVisited ?? 0),
            rows.Sum(item => item.Day?.Kilometres ?? 0),
            rows.Sum(item => item.Day?.AccountSales ?? 0m)
        ]);

        foreach (var (item, offset) in rows.Select((item, offset) => (item, offset)))
        {
            ws.Cell(firstData + offset, 5).Style.Font.FontColor = MgmtToneInk(item.ToDate.TargetAchieved) ?? TsTextDark;
            ws.Cell(firstData + offset, 5).Style.Font.Bold = true;

            if (item.Day?.HasActivity != true)
            {
                ws.Range(firstData + offset, 1, firstData + offset, lastCol).Style.Font.FontColor = TsTextMuted;
            }
        }

        row = MgmtNote(ws, row - 1, lastCol, "* Account sales are keyed in SAP by the office and are not in van sales. Target met is the month to date over the month's target.");
        row++;

        var issues = rows.Where(item => !string.IsNullOrWhiteSpace(item.Day?.Notes)).ToList();
        TsSectionTitle(ws, row, lastCol, issues.Count == 0 ? $"OPERATING ISSUES  |  NONE ENTERED FOR {day:d MMM yyyy}".ToUpperInvariant() : "OPERATING ISSUES");
        row += 2;
        foreach (var item in issues)
        {
            row = MgmtNote(ws, row, lastCol, $"{item.Truck.RouteName} — {item.Day!.Notes}");
        }

        TsFinalize(ws, lastCol, freezeCol: 1);
    }

    private static void BuildMgmtTruckCardsSheet(XLWorkbook workbook, VanManagementReport report, DateTime day, DateTime now)
    {
        var rows = VanManagementReportBuilder.DayView(report, day);
        int lastCol = 1 + rows.Count * 2;
        var ws = workbook.Worksheets.Add("Daily Truck Cards");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"DAILY TRUCK CARDS  |  {day.ToString("dddd d MMMM yyyy").ToUpperInvariant()}", Math.Max(lastCol, 3), now);

        // A truck's name over its Daily and MTD columns, the workbook's card laid side by side.
        ws.Cell(row, 1).Value = "Metric";
        for (int i = 0; i < rows.Count; i++)
        {
            var col = 2 + i * 2;
            ws.Range(row, col, row, col + 1).Merge();
            ws.Cell(row, col).Value = rows[i].Truck.RouteName;
            ws.Cell(row, col).Style.Alignment.WrapText = true;
        }

        var head = ws.Range(row, 1, row, Math.Max(lastCol, 1));
        head.Style.Fill.BackgroundColor = TsNavy;
        head.Style.Font.FontColor = XLColor.White;
        head.Style.Font.Bold = true;
        head.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(row).Height = 32;
        row++;

        var headers = new List<string> { string.Empty };
        foreach (var _ in rows)
        {
            headers.Add("Daily");
            headers.Add("MTD");
        }

        row = TsColumnHeaders(ws, row, lastCol, headers.ToArray());
        ws.Row(row - 1).Height = 18;

        var metrics = new List<(string Label, Func<VanManagementTruckDay, object?> Daily, Func<VanManagementTruckDay, object?> ToDate, string? Format, bool Strong)>
        {
            ("Area worked", item => item.Day?.AreaName, _ => null, null, false),
            ("Customers", item => item.Day?.HasActivity == true ? item.Day.CustomersVisited : null, item => item.ToDate.CustomersVisited, FormatCount, false),
            ("Departure time", item => item.Day?.TimeOut?.TimeOfDay, _ => null, FormatClock, false),
            ("Return time", item => item.Day?.ReturnedLate == true ? null : item.Day?.TimeIn?.TimeOfDay, _ => null, FormatClock, false),
            ("CCR", item => item.Day?.CallComplianceRate, item => item.ToDate.CallComplianceRate, FormatPercent, false),
            ("PCR", item => item.Day?.ProductiveCallRate, item => item.ToDate.ProductiveCallRate, FormatPercent, false),
            ("Average order", item => item.Day?.AverageOrderValue, item => item.ToDate.AverageOrderValue, FormatUsd, false),
            ("Actual day sales", item => item.Day?.HasActivity == true ? item.Day.Sales : null, item => item.ToDate.Sales, FormatUsd, true),
            ("New customers", item => item.Day?.HasActivity == true ? item.Day.NewCustomers : null, item => item.ToDate.NewCustomers, FormatCount, false),
            ("Ecocash", item => item.Day?.HasActivity == true ? item.Day.Ecocash : null, item => item.ToDate.Ecocash, FormatUsd, false),
            ("Account customers", item => item.Day?.AccountSales, item => item.ToDate.AccountSales, FormatUsd, false),
            ("Opening odometer (km)", item => item.Day?.StartingMileage, _ => null, FormatCount, false),
            ("Closing odometer (km)", item => item.Day?.ClosingMileage, _ => null, FormatCount, false),
            ("Total km travelled", item => item.Day?.Kilometres, item => item.ToDate.Kilometres, FormatCount, false),
            ("Fuel allocated (L)", item => item.Day?.FuelLitres, item => item.ToDate.FuelLitres > 0 ? item.ToDate.FuelLitres : null, FormatLitres, false),
            ("Truck target", item => item.Day?.DailySalesTarget, item => item.ToDate.PeriodTarget, FormatUsd, false),
            ("Balance to target", _ => null, item => item.ToDate.BalanceToTarget, FormatUsdSigned, true)
        };

        int index = 0;
        foreach (var metric in metrics)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = metric.Label;
            ws.Cell(row, 1).Style.Font.Bold = true;

            for (int i = 0; i < rows.Count; i++)
            {
                MgmtCell(ws.Cell(row, 2 + i * 2), metric.Daily(rows[i]), metric.Format);
                MgmtCell(ws.Cell(row, 3 + i * 2), metric.ToDate(rows[i]), metric.Format);
            }

            if (metric.Strong)
            {
                ws.Range(row, 1, row, lastCol).Style.Font.Bold = true;
                ws.Range(row, 1, row, lastCol).Style.Fill.BackgroundColor = TsSubHeaderBg;
            }

            // Each card's MTD column sits on a light ground so the pairs read apart.
            for (int i = 0; i < rows.Count; i++)
            {
                ws.Cell(row, 3 + i * 2).Style.Border.RightBorder = XLBorderStyleValues.Medium;
                ws.Cell(row, 3 + i * 2).Style.Border.RightBorderColor = TsGridColor;
            }

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeCol: 1);
        ws.Column(1).Width = 24;
        for (int col = 2; col <= lastCol; col++)
        {
            ws.Column(col).Width = Math.Max(ws.Column(col).Width, 13);
        }
    }

    // — PCR & CCR Trends ———————————————————————————————————————————————————————————————————————

    private const string MgmtCallRatesSheet = "PCR & CCR Trends";

    /// <summary>A change in percentage points: green up, red down.</summary>
    private const string MgmtPoints = "[Color10]\"▲ \"0.0\" pts\";[Red]\"▼ \"0.0\" pts\";0.0\" pts\"";

    /// <summary>The same without colour, for the navy total rows, where green and red do not read.</summary>
    private const string MgmtPointsPlain = "\"▲ \"0.0\" pts\";\"▼ \"0.0\" pts\";0.0\" pts\"";

    /// <summary>
    /// CCR and PCR day by day: the fleet's rate each day against its month to date, the calls behind them,
    /// week on week, and a grid of each truck's day shaded from its lowest to its highest, so a slide or a
    /// recovery shows across a row.
    /// </summary>
    private static void BuildMgmtCallRatesSheet(XLWorkbook workbook, VanManagementReport report, MgmtCharts charts, DateTime now)
    {
        var rates = VanManagementReportBuilder.CallRates(report);
        var dates = rates.Dates;
        int lastCol = Math.Max(12, dates.Count + 2);
        var ws = workbook.Worksheets.Add(MgmtCallRatesSheet);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"CALL RATES  |  CCR AND PCR DAY BY DAY  —  {MgmtPeriod(report).ToUpperInvariant()}", lastCol, now);
        row = MgmtNote(ws, row, lastCol,
            "CCR (call compliance) is the shops on the day's round that were checked into, over the shops on the round. " +
            "PCR (productive calls) is the calls that ended in a sale, over the calls made. A day with no plan or no check-in has no rate and shows a dash, never 0%. " +
            "The month's figures are the Monthly Summary's.");
        row++;

        var total = report.Total;
        var latest = rates.Fleet.LastOrDefault(day => day.Day.CallsMade > 0 || day.Day.PlannedCustomers > 0);
        var (previousWeek, latestWeek) = rates.LastTwoWeeks;
        var ccrWeekChange = MgmtPointsBetween(latestWeek?.Calls.CallComplianceRate, previousWeek?.Calls.CallComplianceRate);
        var pcrWeekChange = MgmtPointsBetween(latestWeek?.Calls.ProductiveCallRate, previousWeek?.Calls.ProductiveCallRate);

        row = MgmtKpiStrip(ws, row, lastCol,
            ("Month CCR", total.CallComplianceRate, MgmtRate, null),
            ("Month PCR", total.ProductiveCallRate, MgmtRate, null),
            (latest is null ? "Latest Day CCR" : $"CCR On {latest.Date:d MMM}", latest?.Day.CallComplianceRate, MgmtRate, null),
            (latest is null ? "Latest Day PCR" : $"PCR On {latest.Date:d MMM}", latest?.Day.ProductiveCallRate, MgmtRate, null),
            ("CCR Week On Week", ccrWeekChange, MgmtPoints, MgmtSignInk(ccrWeekChange)),
            ("PCR Week On Week", pcrWeekChange, MgmtPoints, MgmtSignInk(pcrWeekChange)),
            ("Calls Made", total.PcrCallsMade, MgmtWhole, null));

        var ccrBlock = charts.Block("CCR day by day", ["Day", "CCR on the day", "CCR month to date"], rates.Fleet,
            day => [day.Date.ToString("d MMM"), day.Day.CallComplianceRate, day.ToDate.CallComplianceRate]);
        var pcrBlock = charts.Block("PCR day by day", ["Day", "PCR on the day", "PCR month to date"], rates.Fleet,
            day => [day.Date.ToString("d MMM"), day.Day.ProductiveCallRate, day.ToDate.ProductiveCallRate]);
        var callsBlock = charts.Block("Calls day by day", ["Day", "Productive calls", "Calls with no sale"], rates.Fleet,
            day => [day.Date.ToString("d MMM"), day.Day.CallsMade > 0 ? day.Day.ProductiveCalls : null, day.Day.CallsMade > 0 ? day.Day.CallsMade - day.Day.ProductiveCalls : null]);

        // The day's rate is the light line and the month to date the dark one, so the noise of a single
        // day reads as noise and the trend as the trend.
        charts.Place(MgmtCallRatesSheet, row, 0, new MgmtChartSpec("CCR — the fleet day by day", ccrBlock,
            [new MgmtSeries(2, MgmtSoftBlueHex, Line: true), new MgmtSeries(3, MgmtNavyHex, Line: true)],
            MgmtChartType.Line, AxisFormat: "0%"));
        charts.Place(MgmtCallRatesSheet, row, 1, new MgmtChartSpec("PCR — the fleet day by day", pcrBlock,
            [new MgmtSeries(2, MgmtSoftBlueHex, Line: true), new MgmtSeries(3, MgmtNavyHex, Line: true)],
            MgmtChartType.Line, AxisFormat: "0%"));
        row = MgmtChartBand(row);
        charts.Place(MgmtCallRatesSheet, row, 0, new MgmtChartSpec("Calls made each day, and how many sold", callsBlock,
            [new MgmtSeries(2, MgmtBlueHex), new MgmtSeries(3, MgmtTrackHex)],
            MgmtChartType.Column, Stacked: true, AxisFormat: "#,##0"),
            width: MgmtChartWidth * 2 + MgmtChartGap, height: 300);
        row = MgmtChartBand(row, 300);

        // — The fleet day by day, each rate's change on the last day that had one.
        TsSectionTitle(ws, row, lastCol, "THE FLEET DAY BY DAY");
        row += 2;

        double? lastCcr = null, lastPcr = null;
        var fleetRows = new List<(VanManagementCallRateDay Day, double? CcrChange, double? PcrChange)>();
        foreach (var day in rates.Fleet)
        {
            fleetRows.Add((day, MgmtPointsBetween(day.Day.CallComplianceRate, lastCcr), MgmtPointsBetween(day.Day.ProductiveCallRate, lastPcr)));
            lastCcr = day.Day.CallComplianceRate ?? lastCcr;
            lastPcr = day.Day.ProductiveCallRate ?? lastPcr;
        }

        int fleetFirst = row + 1;
        var month = VanManagementReportBuilder.Calls(report.Truck.Days);
        row = MgmtTable(ws, row, lastCol, fleetRows,
        [
            new("Date", item => item.Day.Date, FormatDayDate),
            new("Trucks Reporting", item => item.Day.TrucksReporting, FormatCount),
            new("Shops Planned", item => item.Day.Day.PlannedCustomers > 0 ? item.Day.Day.PlannedCustomers : null, FormatCount),
            new("Planned Shops Called", item => item.Day.Day.PlannedCustomers > 0 ? item.Day.Day.PlannedCallsMade : null, FormatCount),
            new("CCR", item => item.Day.Day.CallComplianceRate, FormatPercent),
            new("CCR Month To Date", item => item.Day.ToDate.CallComplianceRate, FormatPercent),
            new("CCR Change", item => item.CcrChange, MgmtPoints),
            new("Calls Made", item => item.Day.Day.CallsMade > 0 ? item.Day.Day.CallsMade : null, FormatCount),
            new("Productive Calls", item => item.Day.Day.CallsMade > 0 ? item.Day.Day.ProductiveCalls : null, FormatCount),
            new("PCR", item => item.Day.Day.ProductiveCallRate, FormatPercent),
            new("PCR Month To Date", item => item.Day.ToDate.ProductiveCallRate, FormatPercent),
            new("PCR Change", item => item.PcrChange, MgmtPoints)
        ], total:
        [
            "MONTH", null, month.PlannedCustomers, month.PlannedCallsMade, total.CallComplianceRate, null, null,
            month.CallsMade, month.ProductiveCalls, total.ProductiveCallRate, null, null
        ]);

        if (fleetRows.Count > 0)
        {
            MgmtRateScale(ws.Range(fleetFirst, 5, fleetFirst + fleetRows.Count - 1, 5));
            MgmtRateScale(ws.Range(fleetFirst, 10, fleetFirst + fleetRows.Count - 1, 10));
        }

        row = MgmtNote(ws, row - 1, lastCol, "Change is in percentage points against the last day that had the rate. Shading runs from the month's lowest day (red) to its highest (green).");
        row++;

        // — Week on week.
        TsSectionTitle(ws, row, lastCol, "WEEK ON WEEK  |  THE FIRST AND LAST WEEKS HOLD THIS PERIOD'S DATES ONLY");
        row += 2;
        VanManagementCallRateWeek? priorWeek = null;
        var weekRows = new List<(VanManagementCallRateWeek Week, double? CcrChange, double? PcrChange)>();
        foreach (var week in rates.Weeks.Where(week => week.Calls.CallsMade > 0 || week.Calls.PlannedCustomers > 0))
        {
            weekRows.Add((week,
                MgmtPointsBetween(week.Calls.CallComplianceRate, priorWeek?.Calls.CallComplianceRate),
                MgmtPointsBetween(week.Calls.ProductiveCallRate, priorWeek?.Calls.ProductiveCallRate)));
            priorWeek = week;
        }

        row = MgmtTable(ws, row, lastCol, weekRows,
        [
            new("Week", item => item.Week.Label),
            new("Shops Planned", item => item.Week.Calls.PlannedCustomers, FormatCount),
            new("Planned Shops Called", item => item.Week.Calls.PlannedCallsMade, FormatCount),
            new("CCR", item => item.Week.Calls.CallComplianceRate, FormatPercent),
            new("CCR Change", item => item.CcrChange, MgmtPoints),
            new("Calls Made", item => item.Week.Calls.CallsMade, FormatCount),
            new("Productive Calls", item => item.Week.Calls.ProductiveCalls, FormatCount),
            new("PCR", item => item.Week.Calls.ProductiveCallRate, FormatPercent),
            new("PCR Change", item => item.PcrChange, MgmtPoints)
        ]);

        // — Each truck, day by day.
        row = MgmtTruckRateGrid(ws, row, lastCol, rates, "CCR BY TRUCK, DAY BY DAY", calls => calls.CallComplianceRate,
            truck => truck.CallComplianceRate, total.CallComplianceRate, markWholeList: true);
        row = MgmtTruckRateGrid(ws, row, lastCol, rates, "PCR BY TRUCK, DAY BY DAY", calls => calls.ProductiveCallRate,
            truck => truck.ProductiveCallRate, total.ProductiveCallRate, markWholeList: false);

        // — Each truck, its latest week against the one before.
        var previousIndex = previousWeek is null ? -1 : rates.Weeks.ToList().IndexOf(previousWeek);
        var latestIndex = latestWeek is null ? -1 : rates.Weeks.ToList().IndexOf(latestWeek);
        VanManagementCalls? WeekOf(VanManagementCallRateTruck truck, int index) => index >= 0 ? truck.Weeks[index] : null;

        TsSectionTitle(ws, row, lastCol, latestWeek is null
            ? "EACH TRUCK  |  LATEST WEEK AGAINST THE ONE BEFORE"
            : $"EACH TRUCK  |  {latestWeek.Label.ToUpperInvariant()} AGAINST {(previousWeek?.Label ?? "NO EARLIER WEEK").ToUpperInvariant()}");
        row += 2;
        row = MgmtTable(ws, row, lastCol, rates.Trucks.ToList(),
        [
            new("Truck", truck => truck.Truck.RouteName),
            new("Month CCR", truck => truck.Truck.CallComplianceRate, FormatPercent),
            new("CCR Week Before", truck => WeekOf(truck, previousIndex)?.CallComplianceRate, FormatPercent),
            new("CCR Latest Week", truck => WeekOf(truck, latestIndex)?.CallComplianceRate, FormatPercent),
            new("CCR Change", truck => MgmtPointsBetween(WeekOf(truck, latestIndex)?.CallComplianceRate, WeekOf(truck, previousIndex)?.CallComplianceRate), MgmtPoints),
            new("Month PCR", truck => truck.Truck.ProductiveCallRate, FormatPercent),
            new("PCR Week Before", truck => WeekOf(truck, previousIndex)?.ProductiveCallRate, FormatPercent),
            new("PCR Latest Week", truck => WeekOf(truck, latestIndex)?.ProductiveCallRate, FormatPercent),
            new("PCR Change", truck => MgmtPointsBetween(WeekOf(truck, latestIndex)?.ProductiveCallRate, WeekOf(truck, previousIndex)?.ProductiveCallRate), MgmtPoints)
        ], total:
        [
            "ALL TRUCKS", total.CallComplianceRate, previousWeek?.Calls.CallComplianceRate, latestWeek?.Calls.CallComplianceRate, ccrWeekChange,
            total.ProductiveCallRate, previousWeek?.Calls.ProductiveCallRate, latestWeek?.Calls.ProductiveCallRate, pcrWeekChange
        ]);
        ws.Cell(row - 2, 5).Style.NumberFormat.Format = MgmtPointsPlain;
        ws.Cell(row - 2, 9).Style.NumberFormat.Format = MgmtPointsPlain;

        TsFinalize(ws, lastCol, freezeCol: 1);
        ws.Column(1).Width = Math.Max(ws.Column(1).Width, 30);
    }

    /// <summary>
    /// A truck per row and a day per column, each cell the truck's rate that day, shaded across the whole
    /// grid from its lowest to its highest, with the month's rate at the end.
    /// </summary>
    private static int MgmtTruckRateGrid(
        IXLWorksheet ws,
        int row,
        int lastCol,
        VanManagementCallRates rates,
        string title,
        Func<VanManagementCalls, double?> rate,
        Func<VanDailyTruckSummary, double?> monthRate,
        double? fleetMonth,
        bool markWholeList)
    {
        TsSectionTitle(ws, row, lastCol, title);
        row += 2;

        var columns = new List<MgmtColumn<VanManagementCallRateTruck>> { new("Truck", truck => truck.Truck.RouteName) };
        columns.AddRange(rates.Dates.Select((date, index) =>
            new MgmtColumn<VanManagementCallRateTruck>(date.ToString("ddd d"), truck => rate(truck.Days[index]), FormatPercent)));
        columns.Add(new("Month", truck => monthRate(truck.Truck), FormatPercent));

        int first = row + 1;
        var monthCol = rates.Dates.Count + 2;
        row = MgmtTable(ws, row, lastCol, rates.Trucks.ToList(), columns.ToArray(),
            total: ["FLEET", .. rates.Fleet.Select(day => (object?)rate(day.Day)), fleetMonth]);

        if (rates.Trucks.Count > 0 && rates.Dates.Count > 0)
        {
            MgmtRateScale(ws.Range(first, 2, first + rates.Trucks.Count - 1, rates.Dates.Count + 1));
            ws.Range(first - 1, 2, first + rates.Trucks.Count, rates.Dates.Count + 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Range(first, monthCol, first + rates.Trucks.Count - 1, monthCol).Style.Font.Bold = true;

            if (markWholeList)
            {
                for (int t = 0; t < rates.Trucks.Count; t++)
                {
                    for (int d = 0; d < rates.Dates.Count; d++)
                    {
                        if (rates.Trucks[t].Days[d].OnWholeListOnly)
                        {
                            ws.Cell(first + t, d + 2).Style.Font.Italic = true;
                            ws.Cell(first + t, d + 2).Style.Font.FontColor = TsTextMuted;
                        }
                    }
                }
            }
        }

        if (markWholeList)
        {
            row = MgmtNote(ws, row - 1, lastCol,
                "Grey italics: the truck had no round that day, so its CCR is over its whole customer list and reads low. Shading runs from the grid's lowest rate (red) to its highest (green).");
            row++;
        }

        return row;
    }

    /// <summary>Red at the range's lowest rate, white at its middle, green at its highest.</summary>
    private static void MgmtRateScale(IXLRange range) =>
        range.AddConditionalFormat()
            .ColorScale()
            .LowestValue(XLColor.FromHtml("#F2B8B5"))
            .Midpoint(XLCFContentType.Percentile, "50", XLColor.FromHtml("#FFFFFF"))
            .HighestValue(XLColor.FromHtml("#B9DDBB"));

    /// <summary>The change from one rate to another in percentage points; null unless both are known.</summary>
    private static double? MgmtPointsBetween(double? now, double? before) =>
        now is { } current && before is { } earlier ? Math.Round((current - earlier) * 100, 4) : null;

    private static XLColor? MgmtSignInk(double? change) => change switch
    {
        > 0 => TsGreen,
        < 0 => TsRed,
        _ => null
    };

    // — Weekly Sales ————————————————————————————————————————————————————————————————————————————

    private static void BuildMgmtWeeklySheet(XLWorkbook workbook, VanManagementReport report, DateTime? weekStart, MgmtCharts charts, DateTime now)
    {
        const int lastCol = 6;
        const string sheet = "Weekly Sales";
        var ws = workbook.Worksheets.Add(sheet);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"WEEKLY VAN SALES  |  {MgmtPeriod(report).ToUpperInvariant()}", lastCol, now);
        var week = VanManagementReportBuilder.WeekOf(report, weekStart);

        if (week is not null)
        {
            row = MgmtKpiStrip(ws, row, lastCol,
                ("Week Shown", week.Label, null, null),
                ("Weekly Van Sales", week.Sales, MgmtMoney, null),
                ("Truck-Days Reported", week.Submissions, MgmtWhole, null),
                ("Trucks Reporting", $"{week.TrucksReporting} / {week.Trucks.Count}", null, week.TrucksReporting < week.Trucks.Count ? TsOrange : null),
                ("Account Deliveries", week.AccountSales, MgmtMoney, null));

            var weeksBlock = charts.Block("Van sales by week", ["Week", "Van sales"], report.Weeks, item => [item.Label, item.HasData ? item.Sales : null]);
            var shareBlock = charts.Block($"Week of {week.Label} by truck", ["Truck", "Weekly sales"],
                week.Trucks.OrderByDescending(truck => truck.Sales), item => [item.RouteName, item.Sales]);

            charts.Place(sheet, row, 0, new MgmtChartSpec("Van sales by week", weeksBlock,
                [new MgmtSeries(2, MgmtBlueHex)], MgmtChartType.Column, Labels: true, Legend: false));
            charts.Place(sheet, row, 1, new MgmtChartSpec($"Share of the week, by truck · {week.Label}", shareBlock,
                [new MgmtSeries(2, MgmtBlueHex)], MgmtChartType.Bar, Labels: true, Legend: false));
            row = MgmtChartBand(row);

            TsSectionTitle(ws, row, lastCol, "SELECTED WEEK  |  TRUCK DETAIL");
            row += 2;
            var ordered = week.Trucks.OrderByDescending(truck => truck.Sales).ToList();
            var days = week.Days.Count(d => d.Date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d.Date <= report.ReportDate);
            int firstData = row + 1;
            row = MgmtTable(ws, row, lastCol, ordered,
            [
                new("Truck", truck => truck.RouteName),
                new("Weekly Sales", truck => truck.Sales, FormatUsd),
                new("Days Reported", truck => truck.Submissions, FormatCount),
                new("Share Of Week", truck => truck.Share, FormatPercent)
            ], total: ["TOTAL", week.Sales, week.Submissions, week.Sales > 0 ? 1.0 : null]);

            foreach (var (truck, offset) in ordered.Select((truck, offset) => (truck, offset)))
            {
                if (truck.Submissions < days)
                {
                    ws.Cell(firstData + offset, 3).Style.Font.FontColor = TsOrange;
                    ws.Cell(firstData + offset, 3).Style.Font.Bold = true;
                }
            }

            TsSectionTitle(ws, row, lastCol, "DAILY PATTERN");
            row += 2;
            row = MgmtTable(ws, row, lastCol, week.Days.ToList(),
            [
                new("Date", item => item.Date, FormatDayDate),
                new("Van Sales", item => item.Submissions > 0 ? item.Sales : "—", FormatUsd),
                new("Trucks Reporting", item => item.Submissions, FormatCount)
            ]);
        }

        TsSectionTitle(ws, row, lastCol, "WEEK INDEX  |  THE FIRST AND LAST WEEKS HOLD THIS PERIOD'S DATES ONLY");
        row += 2;
        int indexFirst = row + 1;
        MgmtTable(ws, row, lastCol, report.Weeks.ToList(),
        [
            new("Week Start", item => item.Start, FormatDayDate),
            new("Week End", item => item.End, FormatDayDate),
            new("Van Sales", item => item.HasData ? item.Sales : "—", FormatUsd),
            new("Truck-Days Reported", item => item.Submissions, FormatCount),
            new("Trucks Reporting", item => item.TrucksReporting, FormatCount)
        ]);

        if (week is not null)
        {
            var chosen = report.Weeks.ToList().IndexOf(week);
            if (chosen >= 0)
            {
                ws.Range(indexFirst + chosen, 1, indexFirst + chosen, 5).Style.Fill.BackgroundColor = TsSubHeaderBg;
                ws.Range(indexFirst + chosen, 1, indexFirst + chosen, 5).Style.Font.Bold = true;
            }
        }

        TsFinalize(ws, lastCol);
        ws.Column(1).Width = Math.Max(ws.Column(1).Width, 34);
    }

    // — Route Sales Ranking —————————————————————————————————————————————————————————————————————

    private static void BuildMgmtRoutesSheet(XLWorkbook workbook, VanManagementReport report, MgmtCharts charts, DateTime now)
    {
        const int lastCol = 6;
        const string sheet = "Route Sales Ranking";
        var ws = workbook.Worksheets.Add(sheet);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"{MgmtPeriod(report).ToUpperInvariant()}  |  ROUTE SALES RANKING", lastCol, now);
        var routes = report.Routes;

        row = MgmtKpiStrip(ws, row, lastCol,
            ("Highest", routes.Highest is { } high ? $"{high.Area} · {TruckDollars(high.Sales)}" : "—", null, TsGreen),
            ("Middle", routes.Middle is { } middle ? $"{middle.Area} · {TruckDollars(middle.Sales)}" : "—", null, null),
            ("Lowest", routes.Lowest is { } low ? $"{low.Area} · {TruckDollars(low.Sales)}" : "—", null, TsRed),
            ("Distinct Areas", routes.Ranked.Count, MgmtWhole, null),
            ("Truck-Days Reported", routes.SalesRecords, MgmtWhole, null),
            ("Days With No Area", routes.RecordsWithoutArea, MgmtWhole, routes.RecordsWithoutArea > 0 ? TsOrange : null));

        var topBlock = charts.Block("Top 15 areas", ["Area worked", "Van sales"], routes.Ranked.Take(15), item => [item.Area, item.Sales]);
        charts.Place(sheet, row, 0, new MgmtChartSpec("Top 15 areas by van sales", topBlock,
            [new MgmtSeries(2, MgmtBlueHex)], MgmtChartType.Bar, Labels: true, Legend: false),
            width: MgmtChartWidth * 2 + MgmtChartGap, height: 440);
        row = MgmtChartBand(row, 440);

        row = MgmtNote(ws, row, lastCol,
            "Areas are ranked by the period's van sales. Middle is the upper middle rank when the count is even. An area is named as it was recorded, so spelling variants appear separately; days that named no area are left out.");
        row++;

        int firstData = row + 1;
        MgmtTable(ws, row, lastCol, routes.Ranked.Select((route, rank) => (route, rank)).ToList(),
        [
            new("#", item => item.rank + 1, FormatCount),
            new("Area Worked", item => item.route.Area),
            new("Trucks", item => item.route.Trucks),
            new("Days", item => item.route.Days, FormatCount),
            new("Van Sales (US$)", item => item.route.Sales, FormatUsd),
            new("Per Day", item => item.route.Days > 0 ? item.route.Sales / item.route.Days : null, FormatUsd)
        ]);

        if (routes.Ranked.Count > 0)
        {
            ws.Range(firstData, 5, firstData + routes.Ranked.Count - 1, 5)
                .AddConditionalFormat()
                .DataBar(XLColor.FromHtml("#" + MgmtSoftBlueHex))
                .LowestValue()
                .HighestValue();
        }

        TsFinalize(ws, lastCol, freezeRow: row);
        ws.Column(3).Width = Math.Min(ws.Column(3).Width, 40);
    }

    // — Product group ———————————————————————————————————————————————————————————————————————————

    private static void BuildMgmtProductsSheet(XLWorkbook workbook, VanManagementReport report, MgmtCharts charts, DateTime now)
    {
        const int lastCol = 5;
        var sheet = MgmtSheetName(report.ProductGroupName!);
        var ws = workbook.Worksheets.Add(sheet);
        TsApplyDefaults(ws);

        var products = report.Products;
        int row = TsTitleBar(ws, $"{report.ProductGroupName!.ToUpperInvariant()}  |  UNITS AND SALES VALUE", lastCol, now);

        row = MgmtKpiStrip(ws, row, lastCol,
            ("Units", products.Units, MgmtWhole, null),
            ("Value With VAT", products.Value, MgmtMoney, null),
            ("Share Of Van Sales", report.Total.Sales > 0 ? (double)(products.Value / report.Total.Sales) : null, MgmtRate, null),
            ("Units On No Route", products.UnitsOutsideRoutes, MgmtWhole, products.UnitsOutsideRoutes > 0 ? TsOrange : null));

        // One series per item, the five that sold most and the rest together, so the stack stays legible.
        var items = products.Items.Take(MgmtItemHexes.Length - 1).Select(item => item.ItemCode).ToList();
        var others = products.Items.Count > items.Count;
        var headers = new List<string> { "Truck" };
        headers.AddRange(items.Select(code => products.Items.First(item => item.ItemCode == code).ItemName ?? code));
        if (others)
        {
            headers.Add("Other items");
        }

        var extras = report.Extras?.Products ?? [];
        var unitsBlock = charts.Block($"{report.ProductGroupName} units by truck", headers.ToArray(),
            products.Trucks.OrderByDescending(truck => truck.Units),
            truck =>
            {
                var mine = extras.Where(product => product.RouteId == truck.RouteId).ToList();
                var cells = new List<object?> { truck.RouteName };
                cells.AddRange(items.Select(code => (object?)mine.Where(product => string.Equals(product.ItemCode, code, StringComparison.OrdinalIgnoreCase)).Sum(product => product.Units)));
                if (others)
                {
                    cells.Add(mine.Where(product => !items.Contains(product.ItemCode, StringComparer.OrdinalIgnoreCase)).Sum(product => product.Units));
                }

                return cells.ToArray();
            });

        charts.Place(sheet, row, 0, new MgmtChartSpec("Units by truck and item", unitsBlock,
            Enumerable.Range(0, headers.Count - 1).Select(i => new MgmtSeries(2 + i, MgmtItemHexes[i % MgmtItemHexes.Length])).ToList(),
            MgmtChartType.Bar, Stacked: true, AxisFormat: "#,##0"),
            width: MgmtChartWidth * 2 + MgmtChartGap, height: 380);
        row = MgmtChartBand(row, 380);

        row = MgmtNote(ws, row, lastCol, "Counted from the van invoices' lines in the unit each was sold in. Value is US dollars with VAT, the same money as van sales.");
        row++;

        TsSectionTitle(ws, row, lastCol, "BY TRUCK");
        row += 2;
        row = MgmtTable(ws, row, lastCol, products.Trucks.OrderByDescending(truck => truck.Units).ToList(),
        [
            new("Truck", truck => truck.RouteName),
            new("Units", truck => truck.Units, FormatCount),
            new("Value With VAT", truck => truck.Value, FormatUsd)
        ], total: ["ALL TRUCKS", products.Units, products.Value]);

        TsSectionTitle(ws, row, lastCol, "BY ITEM");
        row += 2;
        row = MgmtTable(ws, row, lastCol, products.Items.ToList(),
        [
            new("SAP Code", item => item.ItemCode),
            new("Item", item => item.ItemName),
            new("Units", item => item.Units, FormatCount),
            new("Value With VAT", item => item.Value, FormatUsd)
        ]);

        TsSectionTitle(ws, row, lastCol, "DAILY UNITS AND VALUE");
        row += 2;
        MgmtTable(ws, row, lastCol, products.Days.ToList(),
        [
            new("Date", item => item.Date, FormatDayDate),
            new("Units", item => item.Units, FormatCount),
            new("Value With VAT", item => item.Value, FormatUsd)
        ]);

        TsFinalize(ws, lastCol);
        ws.Column(1).Width = Math.Max(ws.Column(1).Width, 34);
    }

    // — Breakages ———————————————————————————————————————————————————————————————————————————————

    private static void BuildMgmtBreakagesSheet(XLWorkbook workbook, VanManagementReport report, MgmtCharts charts, DateTime now)
    {
        const int lastCol = 10;
        const string sheet = "Breakages";
        var ws = workbook.Worksheets.Add(sheet);
        TsApplyDefaults(ws);

        var breakages = report.Breakages;
        int row = TsTitleBar(ws, $"VAN SALES  |  MARKET BREAKAGES  —  {MgmtPeriod(report).ToUpperInvariant()}", lastCol, now);

        row = MgmtKpiStrip(ws, row, lastCol,
            ("Reported SKU Lines", breakages.Lines.Count, MgmtWhole, null),
            ("Broken Units", breakages.Units, MgmtWhole, null),
            ("Value Before VAT", breakages.Value, MgmtMoney, TsOrange),
            ("Lines Not Valued", breakages.Unvalued, MgmtWhole, breakages.Unvalued > 0 ? TsOrange : null));

        var causeBlock = charts.Block("Broken units by cause", ["Cause", "Broken units"], breakages.Causes, item => [item.Cause, item.Units]);
        var truckBlock = charts.Block("Broken units by truck", ["Truck", "Broken units"],
            breakages.Trucks.Where(truck => truck.Units > 0).OrderByDescending(truck => truck.Units), item => [item.RouteName, item.Units]);
        charts.Place(sheet, row, 0, new MgmtChartSpec("Broken units by cause", causeBlock,
            [new MgmtSeries(2, MgmtOrangeHex)], MgmtChartType.Bar, Labels: true, AxisFormat: "#,##0", Legend: false));
        charts.Place(sheet, row, 1, new MgmtChartSpec("Broken units by truck", truckBlock,
            [new MgmtSeries(2, MgmtBlueHex)], MgmtChartType.Bar, Labels: true, AxisFormat: "#,##0", Legend: false));
        row = breakages.Lines.Count > 0 ? MgmtChartBand(row) : row;

        row = MgmtNote(ws, row, lastCol,
            "From the handsets' market breakage reports; reports the office rejected are left out. Units are the office's confirmed count where it has counted, else the rep's. " +
            "Value is at the average price before VAT the vans sold the item for in the period, and blank where they sold none of it.");
        row++;

        TsSectionTitle(ws, row, lastCol, "BREAKAGES BY VAN SALES TRUCK");
        row += 2;
        row = MgmtTable(ws, row, lastCol, breakages.Trucks.ToList(),
        [
            new("Truck", truck => truck.RouteName),
            new("Reported SKU Lines", truck => truck.Lines > 0 ? truck.Lines : "—", FormatCount),
            new("Broken Units", truck => truck.Lines > 0 ? truck.Units : "—", FormatCount),
            new("Value Before VAT (USD)", truck => truck.Lines > 0 ? truck.Value : "—", FormatUsd)
        ], total: ["TOTAL REPORTED", breakages.Lines.Count, breakages.Units, breakages.Value]);

        TsSectionTitle(ws, row, lastCol, "BY CAUSE OF BREAKAGE");
        row += 2;
        row = MgmtTable(ws, row, lastCol, breakages.Causes.ToList(),
        [
            new("Cause", cause => cause.Cause),
            new("Lines", cause => cause.Lines, FormatCount),
            new("Broken Units", cause => cause.Units, FormatCount),
            new("Value Before VAT (USD)", cause => cause.Value, FormatUsd)
        ], total: ["ALL CAUSES", breakages.Lines.Count, breakages.Units, breakages.Value]);

        TsSectionTitle(ws, row, lastCol, "BREAKAGES LOG  |  ONE LINE PER BROKEN PRODUCT");
        row += 2;
        var routeNames = report.Trucks.Where(truck => truck.RouteId.HasValue).ToDictionary(truck => truck.RouteId!.Value, truck => truck.RouteName);
        MgmtTable(ws, row, lastCol, breakages.Lines.ToList(),
        [
            new("Date", line => line.TradingDate.Date, FormatDayDate),
            new("Truck", line => line.RouteId is { } id && routeNames.TryGetValue(id, out var routeName) ? routeName : "No route"),
            new("Rep", line => line.ReportedBy),
            new("Shop", line => line.CustomerName),
            new("SAP Code", line => line.ItemCode),
            new("Product", line => line.ItemDescription),
            new("Cause", line => line.Reason),
            new("Units", line => line.Units, FormatCount),
            new("Value Before VAT", line => line.ValueBeforeVat, FormatUsd),
            new("Status", line => line.IsConfirmed ? line.Status : $"{line.Status} (rep's count)")
        ]);

        TsFinalize(ws, lastCol);
    }

    private const string MgmtMoney = "$#,##0";
    private const string MgmtRate = "0.0%";
    private const string MgmtWhole = "#,##0";

    /// <summary>
    /// The KPI strip with its figures as numbers: written as text, Excel flags each as a number stored as
    /// text and a share loses its decimal. A null is an em dash; text stays text.
    /// </summary>
    private static int MgmtKpiStrip(IXLWorksheet ws, int row, int lastCol, params (string Label, object? Value, string? Format, XLColor? Color)[] kpis)
    {
        var next = TsKpiStrip(ws, row, lastCol, kpis.Select(kpi => (kpi.Label, kpi.Value?.ToString() ?? "—", kpi.Color)).ToArray());

        for (int i = 0; i < kpis.Length && i < lastCol; i++)
        {
            if (kpis[i].Value is not (null or string))
            {
                MgmtCell(ws.Cell(row, i + 1), kpis[i].Value, kpis[i].Format);
            }
        }

        return next;
    }

    // — Sheet helpers ———————————————————————————————————————————————————————————————————————————

    /// <summary>Each truck's month target, sales and balance, which three charts read.</summary>
    private static MgmtBlock MgmtTruckBlock(MgmtCharts charts, VanManagementReport report) =>
        charts.Block("Trucks against target", ["Truck", "Month target", "Van sales", "Balance to target"],
            report.Ranked,
            truck => [truck.RouteName, truck.PeriodTarget, truck.Sales, truck.BalanceToTarget is { } balance ? Math.Max(0m, balance) : null]);

    /// <summary>Leaves the rows a band of charts covers empty; returns the row after it.</summary>
    private static int MgmtChartBand(int row, int height = MgmtChartHeight) =>
        row + (int)Math.Ceiling(height / (double)MgmtRowPixels) + 2;

    /// <summary>A numbered finding: the number on navy, the title in caps and the text under it, across the sheet.</summary>
    private static int MgmtFinding(IXLWorksheet ws, int row, int lastCol, int number, VanManagementInsight insight)
    {
        var numberCell = ws.Cell(row, 1);
        numberCell.Value = number;
        numberCell.Style.Fill.BackgroundColor = TsNavy;
        numberCell.Style.Font.FontColor = XLColor.White;
        numberCell.Style.Font.Bold = true;
        numberCell.Style.Font.FontSize = 14;
        numberCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        numberCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Range(row, 1, row + 1, 1).Merge();

        var title = ws.Range(row, 2, row, lastCol).Merge();
        title.Value = insight.Title.ToUpperInvariant();
        title.Style.Font.Bold = true;
        title.Style.Font.FontColor = TsNavy;
        title.Style.Fill.BackgroundColor = TsStripeBg;

        var body = ws.Range(row + 1, 2, row + 1, lastCol).Merge();
        body.Value = insight.Body;
        body.Style.Alignment.WrapText = true;
        body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        body.Style.Fill.BackgroundColor = TsStripeBg;
        ws.Row(row + 1).Height = insight.Body.Length > 150 ? 42 : 30;

        var card = ws.Range(row, 1, row + 1, lastCol);
        card.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        card.Style.Border.OutsideBorderColor = TsGridLight;

        return row + 3;
    }

    /// <summary>A target share's colour: green at 100%, navy from 50%, amber under 50%, red under 35%.</summary>
    private static XLColor? MgmtToneInk(double? rate) => rate switch
    {
        null => null,
        >= 1 => XLColor.FromHtml("#1E5E22"),
        >= 0.5 => TsNavy,
        >= 0.35 => XLColor.FromHtml("#8A3F0B"),
        _ => XLColor.FromHtml("#A31F1F")
    };

    /// <summary>The same bands as a filled cell, for a column of shares read down the page.</summary>
    private static void MgmtTone(IXLCell cell, double? rate)
    {
        if (rate is not { } value)
        {
            return;
        }

        var fill = value >= 1 ? "#E3F1E4" : value >= 0.5 ? "#DCE6F0" : value >= 0.35 ? "#FBE7D3" : "#F8D7D7";
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(fill);
        cell.Style.Font.FontColor = MgmtToneInk(value);
        cell.Style.Font.Bold = true;
    }

    /// <summary>
    /// Writes a header row, a striped row per item and, when given, a total row; returns the row after,
    /// leaving one blank. A null value leaves the cell empty, except a rate column, where it is an em
    /// dash: nothing under it, never 0%.
    /// </summary>
    private static int MgmtTable<TRow>(
        IXLWorksheet ws,
        int row,
        int lastCol,
        IReadOnlyList<TRow> items,
        MgmtColumn<TRow>[] columns,
        object?[]? total = null,
        Func<TRow, int, string?>? formatOf = null)
    {
        int width = Math.Max(columns.Length, 1);
        row = TsColumnHeaders(ws, row, width, columns.Select(column => column.Header).ToArray());
        ws.Row(row - 1).Height = 30;

        int index = 0;
        foreach (var item in items)
        {
            TsDataRow(ws, row, width, index % 2 == 1);

            for (int col = 1; col <= columns.Length; col++)
            {
                var column = columns[col - 1];
                var cell = ws.Cell(row, col);
                var value = column.Value(item);
                MgmtCell(cell, value, formatOf?.Invoke(item, col) ?? column.Format);

                // A dash standing in for a figure lines up with the figures.
                if (value is "—")
                {
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    cell.Style.Font.FontColor = TsTextMuted;
                }
            }

            row++;
            index++;
        }

        if (items.Count == 0)
        {
            ws.Range(row, 1, row, width).Merge();
            ws.Cell(row, 1).Value = "Nothing recorded in the period.";
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
            row++;
        }

        if (total is not null)
        {
            for (int col = 1; col <= Math.Min(total.Length, columns.Length); col++)
            {
                MgmtCell(ws.Cell(row, col), total[col - 1], columns[col - 1].Format);
            }

            TsSummaryRow(ws, row, width);
            row++;
        }

        return row + 1;
    }

    private static void MgmtCell(IXLCell cell, object? value, string? format)
    {
        switch (value)
        {
            case null when format == FormatPercent:
                cell.Value = "—";
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                return;
            case null:
                return;
            case string text:
                cell.Value = text;
                return;
            case decimal number:
                cell.Value = number;
                break;
            case int number:
                cell.Value = number;
                break;
            case double number:
                cell.Value = number;
                break;
            case DateTime date:
                cell.Value = date;
                break;
            case TimeSpan time:
                cell.Value = time;
                break;
            default:
                cell.Value = value.ToString();
                return;
        }

        if (format is not null)
        {
            cell.Style.NumberFormat.Format = format;
        }
    }

    private static int MgmtNote(IXLWorksheet ws, int row, int lastCol, string text)
    {
        ws.Range(row, 1, row, lastCol).Merge();
        var cell = ws.Cell(row, 1);
        cell.Value = text;
        cell.Style.Font.FontSize = 9;
        cell.Style.Font.Italic = true;
        cell.Style.Font.FontColor = TsTextMuted;
        cell.Style.Alignment.WrapText = true;
        ws.Row(row).Height = text.Length > 140 ? 30 : 16;
        return row + 1;
    }

    private static string MgmtPeriod(VanManagementReport report)
    {
        var from = report.Truck.FromDate.Date;
        var to = report.Truck.ToDate.Date;

        return from.Day == 1 && to == from.AddMonths(1).AddDays(-1)
            ? from.ToString("MMMM yyyy")
            : $"{from:d MMM yyyy} to {to:d MMM yyyy}";
    }

    /// <summary>A sheet name Excel accepts: 31 characters, none of <c>: \ / ? * [ ]</c>, and not one of the fixed sheets.</summary>
    private static string MgmtSheetName(string name)
    {
        var clean = new string(name.Where(ch => ch is not (':' or '\\' or '/' or '?' or '*' or '[' or ']')).ToArray()).Trim();
        if (clean.Length == 0)
        {
            clean = "Product Group";
        }

        if (clean.Length > 31)
        {
            clean = clean[..31];
        }

        string[] taken = ["Insights Summary", "Management Overview", "Monthly Summary", "Daily Management View", "Daily Truck Cards", MgmtCallRatesSheet, "Weekly Sales", "Route Sales Ranking", "Breakages", "Raw Data", "Trucks", "Days", MgmtChartDataSheet];
        return taken.Contains(clean, StringComparer.OrdinalIgnoreCase) ? $"{clean[..Math.Min(clean.Length, 27)]} (2)" : clean;
    }

    // — Charts ——————————————————————————————————————————————————————————————————————————————————

    /// <summary>The cell a point so many pixels from the sheet's left edge falls in, and how far into it.</summary>
    private static (int Column, long OffsetEmu) MgmtColumnAt(IXLWorksheet ws, int pixels)
    {
        int column = 1;
        double left = 0;

        while (column < 200)
        {
            var width = ws.Column(column).Width * 7 + 5;
            if (left + width > pixels)
            {
                break;
            }

            left += width;
            column++;
        }

        return (column - 1, (long)((pixels - left) * 9525));
    }

    private static byte[] AddMgmtCharts(
        byte[] workbookBytes,
        IReadOnlyList<MgmtPlacement> placements,
        IReadOnlyDictionary<MgmtPlacement, (int Column, long OffsetEmu)> anchors)
    {
        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            // ClosedXML writes an Excel 2010 data bar's id in lower case; Excel repairs that by dropping the bars.
            UppercaseDataBarIds(document, ["Insights Summary", "Route Sales Ranking"]);

            if (document.WorkbookPart is { } workbookPart)
            {
                uint axis = 72_000_000;

                foreach (var placement in placements)
                {
                    if (GetWorksheetPartByName(workbookPart, placement.Sheet) is not { } sheet)
                    {
                        continue;
                    }

                    var drawings = EnsureDrawingsPart(sheet);
                    var chartPart = drawings.AddNewPart<ChartPart>();
                    BuildMgmtChart(chartPart, placement.Spec, axis);
                    axis += 10;

                    var (column, offset) = anchors[placement];
                    AppendMgmtChartAnchor(drawings, chartPart, placement.Spec.Title, column, offset, placement.Row - 1, placement.Width, placement.Height);
                }
            }
        }

        return stream.ToArray();
    }

    /// <summary>A chart pinned at its top-left cell, at a fixed size, whatever the columns under it measure.</summary>
    private static void AppendMgmtChartAnchor(
        DrawingsPart drawingsPart, ChartPart chartPart, string name, int column, long columnOffset, int row, int widthPx, int heightPx)
    {
        drawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();

        var drawingId = (uint)(drawingsPart.WorksheetDrawing.ChildElements.Count + 2);
        var anchor = drawingsPart.WorksheetDrawing.AppendChild(new Xdr.OneCellAnchor());
        anchor.Append(new Xdr.FromMarker(
            new Xdr.ColumnId(column.ToString()),
            new Xdr.ColumnOffset(columnOffset.ToString()),
            new Xdr.RowId(row.ToString()),
            new Xdr.RowOffset("0")));
        anchor.Append(new Xdr.Extent { Cx = widthPx * 9525L, Cy = heightPx * 9525L });

        var frame = anchor.AppendChild(new Xdr.GraphicFrame { Macro = string.Empty });
        frame.Append(new Xdr.NonVisualGraphicFrameProperties(
            new Xdr.NonVisualDrawingProperties { Id = drawingId, Name = name },
            new Xdr.NonVisualGraphicFrameDrawingProperties()));

        var transform = new Xdr.Transform();
        transform.Append(new A.Offset { X = 0L, Y = 0L });
        transform.Append(new A.Extents { Cx = 0L, Cy = 0L });
        frame.Append(transform);

        var graphicData = new A.GraphicData { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" };
        graphicData.Append(new C.ChartReference { Id = drawingsPart.GetIdOfPart(chartPart) });
        frame.Append(new A.Graphic(graphicData));

        anchor.Append(new Xdr.ClientData());
        drawingsPart.WorksheetDrawing.Save();
    }

    /// <summary>
    /// One chart over a Chart Data block: columns or bars, with any series marked a line drawn over them
    /// on the same axes; a line chart; or a doughnut. Sideways bars list their categories top-down in the
    /// block's order, as the tables beside them do.
    /// </summary>
    private static void BuildMgmtChart(ChartPart chartPart, MgmtChartSpec spec, uint axisBase)
    {
        var chartSpace = new C.ChartSpace();
        chartSpace.Append(new C.EditingLanguage { Val = "en-US" });
        chartSpace.Append(new C.RoundedCorners { Val = false });

        var chart = chartSpace.AppendChild(new C.Chart());
        chart.Append(RsdChartTitle(spec.Title));
        chart.Append(new C.AutoTitleDeleted { Val = false });

        var plotArea = chart.AppendChild(new C.PlotArea());
        plotArea.Append(new C.Layout());

        var block = spec.Block;
        var categories = BuildSheetRangeFormula(MgmtChartDataSheet, block.FirstRow, 1, block.LastRow, 1);
        C.SeriesText Name(MgmtSeries series) =>
            new(new C.StringReference(new C.Formula(BuildSheetCellFormula(MgmtChartDataSheet, block.HeaderRow, series.Column))));
        C.Values Values(MgmtSeries series) =>
            new(new C.NumberReference(new C.Formula(BuildSheetRangeFormula(MgmtChartDataSheet, block.FirstRow, series.Column, block.LastRow, series.Column))));

        if (spec.Type == MgmtChartType.Doughnut)
        {
            var doughnut = plotArea.AppendChild(new C.DoughnutChart());
            doughnut.Append(new C.VaryColors { Val = true });

            var series = new C.PieChartSeries(new C.Index { Val = 0 }, new C.Order { Val = 0 }, Name(spec.Series[0]));
            for (var point = 0; point <= block.LastRow - block.FirstRow; point++)
            {
                series.Append(new C.DataPoint(
                    new C.Index { Val = (uint)point },
                    new C.Bubble3D { Val = false },
                    new C.ChartShapeProperties(
                        new A.SolidFill(new A.RgbColorModelHex { Val = MgmtTenderHexes[point % MgmtTenderHexes.Length] }),
                        new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "FFFFFF" })))));
            }

            series.Append(new C.DataLabels(
                new C.NumberingFormat { FormatCode = "0.0%", SourceLinked = false },
                RsdChartText(900),
                new C.ShowLegendKey { Val = false },
                new C.ShowValue { Val = false },
                new C.ShowCategoryName { Val = false },
                new C.ShowSeriesName { Val = false },
                new C.ShowPercent { Val = true },
                new C.ShowBubbleSize { Val = false },
                new C.ShowLeaderLines { Val = true }));
            series.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categories))));
            series.Append(Values(spec.Series[0]));
            doughnut.Append(series);
            doughnut.Append(new C.FirstSliceAngle { Val = 0 });
            doughnut.Append(new C.HoleSize { Val = 58 });

            chart.Append(RsdLegend(C.LegendPositionValues.Right));
            chart.Append(new C.PlotVisibleOnly { Val = true });
            chart.Append(new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });
            chartPart.ChartSpace = chartSpace;
            chartPart.ChartSpace.Save();
            return;
        }

        var categoryAxisId = axisBase + 1;
        var valueAxisId = axisBase + 2;
        var horizontal = spec.Type == MgmtChartType.Bar;
        var bars = spec.Series.Where(series => !series.Line && spec.Type != MgmtChartType.Line).ToList();
        var lines = spec.Series.Where(series => series.Line || spec.Type == MgmtChartType.Line).ToList();
        uint index = 0;

        if (bars.Count > 0)
        {
            var barChart = plotArea.AppendChild(new C.BarChart());
            barChart.Append(new C.BarDirection { Val = horizontal ? C.BarDirectionValues.Bar : C.BarDirectionValues.Column });
            barChart.Append(new C.BarGrouping { Val = spec.Stacked ? C.BarGroupingValues.Stacked : C.BarGroupingValues.Clustered });
            barChart.Append(new C.VaryColors { Val = false });

            foreach (var bar in bars)
            {
                var series = new C.BarChartSeries(
                    new C.Index { Val = index },
                    new C.Order { Val = index },
                    Name(bar),
                    new C.ChartShapeProperties(
                        new A.SolidFill(new A.RgbColorModelHex { Val = bar.Color }),
                        new A.Outline(new A.NoFill())),
                    new C.InvertIfNegative { Val = false });
                series.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categories))));
                series.Append(Values(bar));
                barChart.Append(series);
                index++;
            }

            if (spec.Labels)
            {
                barChart.Append(new C.DataLabels(
                    new C.NumberingFormat { FormatCode = spec.AxisFormat, SourceLinked = false },
                    RsdChartText(900),
                    new C.DataLabelPosition { Val = C.DataLabelPositionValues.OutsideEnd },
                    new C.ShowLegendKey { Val = false },
                    new C.ShowValue { Val = true },
                    new C.ShowCategoryName { Val = false },
                    new C.ShowSeriesName { Val = false },
                    new C.ShowPercent { Val = false },
                    new C.ShowBubbleSize { Val = false }));
            }

            barChart.Append(new C.GapWidth { Val = (ushort)(spec.Stacked || spec.Overlap ? 45 : 70) });
            if (spec.Stacked || spec.Overlap)
            {
                // Stacked series sit end to end, and an overlapped sales bar sits over its target, only with a full overlap.
                barChart.Append(new C.Overlap { Val = 100 });
            }

            barChart.Append(new C.AxisId { Val = categoryAxisId });
            barChart.Append(new C.AxisId { Val = valueAxisId });
        }

        if (lines.Count > 0)
        {
            var lineChart = plotArea.AppendChild(new C.LineChart());
            lineChart.Append(new C.Grouping { Val = C.GroupingValues.Standard });
            lineChart.Append(new C.VaryColors { Val = false });

            foreach (var line in lines)
            {
                var outline = new A.Outline { Width = 28575, CapType = A.LineCapValues.Round };
                if (line.MarkersOnly)
                {
                    outline.Append(new A.NoFill());
                }
                else
                {
                    outline.Append(new A.SolidFill(new A.RgbColorModelHex { Val = line.Color }));
                }

                if (line.Dashed)
                {
                    outline.Append(new A.PresetDash { Val = A.PresetLineDashValues.Dash });
                }

                var marker = line.MarkersOnly
                    ? new C.Marker(
                        new C.Symbol { Val = C.MarkerStyleValues.Dash },
                        new C.Size { Val = 20 },
                        new C.ChartShapeProperties(
                            new A.SolidFill(new A.RgbColorModelHex { Val = line.Color }),
                            new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = line.Color }))))
                    : new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.None });

                var series = new C.LineChartSeries(
                    new C.Index { Val = index },
                    new C.Order { Val = index },
                    Name(line),
                    new C.ChartShapeProperties(outline),
                    marker);
                series.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categories))));
                series.Append(Values(line));
                series.Append(new C.Smooth { Val = false });
                lineChart.Append(series);
                index++;
            }

            lineChart.Append(new C.ShowMarker { Val = lines.Any(line => line.MarkersOnly) });
            lineChart.Append(new C.AxisId { Val = categoryAxisId });
            lineChart.Append(new C.AxisId { Val = valueAxisId });
        }

        plotArea.Append(new C.CategoryAxis(
            new C.AxisId { Val = categoryAxisId },
            new C.Scaling(new C.Orientation { Val = horizontal ? C.OrientationValues.MaxMin : C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = horizontal ? C.AxisPositionValues.Left : C.AxisPositionValues.Bottom },
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.MajorTickMark { Val = C.TickMarkValues.None },
            new C.MinorTickMark { Val = C.TickMarkValues.None },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.Low },
            new C.ChartShapeProperties(new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "8A97A6" }))),
            RsdChartText(900),
            new C.CrossingAxis { Val = valueAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = 100 },
            new C.NoMultiLevelLabels { Val = false }));

        plotArea.Append(new C.ValueAxis(
            new C.AxisId { Val = valueAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = horizontal ? C.AxisPositionValues.Bottom : C.AxisPositionValues.Left },
            new C.MajorGridlines(new C.ChartShapeProperties(new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "DDE3EA" })))),
            new C.NumberingFormat { FormatCode = spec.AxisFormat, SourceLinked = false },
            new C.MajorTickMark { Val = C.TickMarkValues.None },
            new C.MinorTickMark { Val = C.TickMarkValues.None },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.ChartShapeProperties(new A.Outline(new A.NoFill())),
            RsdChartText(900),
            new C.CrossingAxis { Val = categoryAxisId },
            // With the categories read top-down, the value axis has to cross at their far end to stay at the bottom.
            new C.Crosses { Val = horizontal ? C.CrossesValues.Maximum : C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between }));

        if (spec.Legend)
        {
            chart.Append(RsdLegend(C.LegendPositionValues.Bottom));
        }

        chart.Append(new C.PlotVisibleOnly { Val = true });
        chart.Append(new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });

        chartPart.ChartSpace = chartSpace;
        chartPart.ChartSpace.Save();
    }
}
