using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The van sales management report as a workbook, laid out as the office's "Vansales Daily Report"
/// workbook was: Insights Summary, Management Overview, Monthly Summary, Daily Management View, Daily
/// Truck Cards, Weekly Sales, Route Sales Ranking, the product group, Breakages and Raw Data.
/// </summary>
/// <remarks>
/// Every figure is the report the page shows, built once by <see cref="VanManagementReportBuilder"/>.
/// Monthly Summary and Raw Data are the Daily Truck Report's own two sheets, so the two workbooks agree
/// to the cent. Money is a number, a blank is "not recorded", and a rate with nothing under it is an em
/// dash, as on that workbook.
/// </remarks>
public partial class ReportExportService
{
    /// <summary>One column of a management table: its heading, its value for a row, and how to show it.</summary>
    private sealed record MgmtColumn<TRow>(string Header, Func<TRow, object?> Value, string? Format = null);

    public byte[] ExportVanManagementReportToExcel(VanManagementReport report, DateTime day, DateTime? weekStart)
    {
        using var workbook = NewWorkbook("Van Sales Management Report");
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildMgmtInsightsSheet(workbook, report, now);
        BuildMgmtOverviewSheet(workbook, report, now);

        BuildVanDailyTruckSummarySheet(workbook, report.Truck, now);
        workbook.Worksheet("Trucks").Name = "Monthly Summary";

        BuildMgmtDailySheet(workbook, report, day, now);
        BuildMgmtTruckCardsSheet(workbook, report, day, now);
        BuildMgmtWeeklySheet(workbook, report, weekStart, now);
        BuildMgmtRoutesSheet(workbook, report, now);

        if (report.ProductGroupName is not null)
        {
            BuildMgmtProductsSheet(workbook, report, now);
        }

        BuildMgmtBreakagesSheet(workbook, report, now);

        BuildVanDailyTruckDaysSheet(workbook, report.Truck, now);
        workbook.Worksheet("Days").Name = "Raw Data";

        return WorkbookToBytes(workbook);
    }

    // — Insights Summary ————————————————————————————————————————————————————————————————————————

    private static void BuildMgmtInsightsSheet(XLWorkbook workbook, VanManagementReport report, DateTime now)
    {
        const int lastCol = 7;
        var ws = workbook.Worksheets.Add("Insights Summary");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"{MgmtPeriod(report).ToUpperInvariant()}  |  SALES PERFORMANCE INSIGHTS", lastCol, now);
        row = MgmtNote(ws, row, lastCol,
            $"Reporting through {report.ReportDate:d MMMM yyyy}  |  {report.Trucks.Count} trucks  |  {report.Routes.SalesRecords} truck-days reported  |  {report.Total.SalesCount:N0} invoices. " +
            "Van sales include Ecocash and exclude account customers, which are reported separately.");
        row++;

        var total = report.Total;
        row = TsKpiStrip(ws, row, lastCol,
            ("Van Sales To Date", TruckDollars(total.Sales), null),
            ("Target Achieved", RateText(total.TargetAchieved), null),
            ("Gap To Target", TruckDollars(total.BalanceToTarget), null),
            ("Daily Run-Rate", TruckDollars(report.DailyRunRate), null),
            ("Needed Per Day Left", TruckDollars(report.RequiredPerRemainingDay), null),
            ("Customers", total.CustomersVisited.ToString("N0"), null),
            ("Km", total.Kilometres > 0 ? total.Kilometres.ToString("N0") : "—", null));

        int index = 1;
        foreach (var insight in report.Insights)
        {
            TsSectionTitle(ws, row, lastCol, $"{index}  |  {insight.Title.ToUpperInvariant()}");
            row++;
            ws.Range(row, 1, row, lastCol).Merge();
            ws.Cell(row, 1).Value = insight.Body;
            ws.Cell(row, 1).Style.Alignment.WrapText = true;
            ws.Cell(row, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ws.Row(row).Height = 46;
            row += 2;
            index++;
        }

        TsSectionTitle(ws, row, lastCol, "TERRITORY PERFORMANCE  |  RANKED BY SALES");
        row += 2;
        row = MgmtTable(ws, row, lastCol, report.Ranked.Select((truck, rank) => (truck, rank)).ToList(),
        [
            new("#", item => item.rank + 1, FormatCount),
            new("Truck", item => item.truck.RouteName),
            new("Van Sales", item => item.truck.Sales, FormatUsd),
            new("Target Achieved", item => item.truck.TargetAchieved, FormatPercent),
            new("Sales / Km", item => item.truck.SalesPerKilometre, FormatUsd),
            new("PCR", item => item.truck.ProductiveCallRate, FormatPercent),
            new("Average Order", item => item.truck.AverageOrderValue, FormatUsd)
        ]);
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
        row++;

        TsSectionTitle(ws, row, lastCol, report.OperatingIssues.Count == 0
            ? "OPERATING ISSUES  |  NONE RECORDED"
            : $"OPERATING ISSUES  |  {report.OperatingIssues.Count} FLAGGED TRUCK-DAYS — SEE MANAGEMENT OVERVIEW");

        TsFinalize(ws, lastCol);
        ws.Column(2).Width = 28;
    }

    // — Management Overview —————————————————————————————————————————————————————————————————————

    private static void BuildMgmtOverviewSheet(XLWorkbook workbook, VanManagementReport report, DateTime now)
    {
        const int lastCol = 6;
        var ws = workbook.Worksheets.Add("Management Overview");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"VAN SALES  |  MANAGEMENT OVERVIEW  —  {MgmtPeriod(report)}", lastCol, now);
        var total = report.Total;

        row = TsKpiStrip(ws, row, lastCol,
            ("Full-Month Target", TruckDollars(total.PeriodTarget), null),
            ("Van Sales To Date", TruckDollars(total.Sales), null),
            ("Balance To Target", TruckDollars(total.BalanceToTarget), null),
            ("Target Achieved", RateText(total.TargetAchieved), null),
            ("Km A Litre", total.KilometresPerLitre is { } kmL ? kmL.ToString("N2") : "—", null),
            ("Fuel Issued (L)", total.FuelLitres > 0 ? total.FuelLitres.ToString("N0") : "—", null));

        TsSectionTitle(ws, row, lastCol, "EACH TRUCK AGAINST ITS TARGET");
        row += 2;
        row = MgmtTable(ws, row, lastCol, report.Trucks.ToList(),
        [
            new("Truck", truck => truck.RouteName),
            new("Monthly Target", truck => truck.PeriodTarget, FormatUsd),
            new("Van Sales To Date", truck => truck.Sales, FormatUsd),
            new("Balance", truck => truck.BalanceToTarget, FormatUsdSigned),
            new("% Target", truck => truck.TargetAchieved, FormatPercent),
            new("Days Reported", truck => $"{truck.DaysReported} of {truck.WorkingDaysElapsed}")
        ], total: ["ALL TRUCKS", total.PeriodTarget, total.Sales, total.BalanceToTarget, total.TargetAchieved, $"{total.DaysReported} of {total.WorkingDaysElapsed}"]);
        row++;

        TsSectionTitle(ws, row, lastCol, "MONTH-TO-DATE VANSALES PAYMENT SUMMARY");
        row += 2;
        row = MgmtNote(ws, row, lastCol, "Van sales include Ecocash. Cash sales are van sales less Ecocash; account deliveries are their own figure and not in van sales.");
        var payments = report.Payments;
        var paymentRows = new List<(string Label, object? Value, string Format)>
        {
            ("Vansales total sales", payments.VanSales, FormatUsd),
            ("Ecocash for the period", payments.Ecocash, FormatUsd),
            ("Vansales cash sales (total less Ecocash)", payments.LessEcocash, FormatUsd),
            ("   of which recorded as cash", payments.Cash, FormatUsd),
            ("   InnBucks", payments.Innbucks, FormatUsd),
            ("   Card, transfer or other", payments.OtherTender, FormatUsd),
            ("   No tender recorded", payments.Untendered, FormatUsd),
            ("Account customer deliveries by vansales", payments.AccountSales, FormatUsd),
            ("Accounts / van sales", payments.AccountsShare, FormatPercent),
            ("Check: total less Ecocash less cash sales", payments.VanSales - payments.Ecocash - payments.LessEcocash, FormatUsd)
        };
        row = MgmtTable(ws, row, lastCol, paymentRows,
        [
            new("Figure", item => item.Label),
            new("Amount", item => item.Value, null)
        ], formatOf: (item, col) => col == 2 ? item.Format : null);
        row++;

        TsSectionTitle(ws, row, lastCol, "ACCOUNT CUSTOMER SALES  |  SEPARATE FROM VAN SALES");
        row += 2;
        row = MgmtTable(ws, row, lastCol, report.Trucks.ToList(),
        [
            new("Truck", truck => truck.RouteName),
            new("Account Sales (USD)", truck => truck.AccountSales, FormatUsd),
            new("Van Sales (USD)", truck => truck.Sales, FormatUsd),
            new("Accounts / Van Sales", truck => truck.Sales > 0 ? (double)(truck.AccountSales / truck.Sales) : null, FormatPercent)
        ], total: ["ALL TRUCKS", total.AccountSales, total.Sales, payments.AccountsShare]);
        row++;

        TsSectionTitle(ws, row, lastCol, "AUTOMATIC MANAGEMENT EXCEPTION MONITOR");
        row += 2;
        row = MgmtTable(ws, row, lastCol, report.ExceptionChecks.ToList(),
        [
            new("Rule", check => check.Rule),
            new("Truck-Days", check => check.Count, FormatCount),
            new("Action", check => check.Count > 0 ? "Review" : "—"),
            new("What It Means", check => check.Meaning)
        ]);
        row++;

        TsSectionTitle(ws, row, lastCol, "OPERATING ISSUES LOG  |  ENTERED AS A NOTE ON THE TRUCK'S DAY");
        row += 2;
        if (report.OperatingIssues.Count == 0)
        {
            MgmtNote(ws, row, lastCol, "None recorded in the period.");
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

    private static void BuildMgmtDailySheet(XLWorkbook workbook, VanManagementReport report, DateTime day, DateTime now)
    {
        const int lastCol = 8;
        var ws = workbook.Worksheets.Add("Daily Management View");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"DAILY MANAGEMENT  |  {day:dddd d MMMM yyyy}", lastCol, now);
        var rows = VanManagementReportBuilder.DayView(report, day);
        var reporting = rows.Count(item => item.Day?.HasActivity == true);
        var daySales = rows.Sum(item => item.Day?.Sales ?? 0m);
        var monthToDate = rows.Sum(item => item.ToDate.Sales);

        row = TsKpiStrip(ws, row, lastCol,
            ("Daily Van Sales", TruckDollars(daySales), null),
            ("Trucks Reporting", $"{reporting} / {rows.Count}", reporting < rows.Count ? TsOrange : null),
            ("MTD Van Sales", TruckDollars(monthToDate), null),
            ("Customers", rows.Sum(item => item.Day?.CustomersVisited ?? 0).ToString("N0"), null),
            ("Km Today", rows.Sum(item => item.Day?.Kilometres ?? 0).ToString("N0"), null));

        row = MgmtTable(ws, row, lastCol, rows.ToList(),
        [
            new("Truck", item => item.Truck.RouteName),
            new("Area Worked", item => item.Day?.AreaName),
            new("Daily Sales", item => item.Day?.HasActivity == true ? item.Day.Sales : "not reported", FormatUsd),
            new("MTD Sales", item => item.ToDate.Sales, FormatUsd),
            new("Target Met", item => item.ToDate.TargetAchieved, FormatPercent),
            new("Customers", item => item.Day?.HasActivity == true ? item.Day.CustomersVisited : null, FormatCount),
            new("Km Today", item => item.Day?.Kilometres, FormatCount),
            new("Account Sales*", item => item.Day?.AccountSales, FormatUsd)
        ], total:
        [
            "TOTAL", null, daySales, monthToDate,
            report.Total.PeriodTarget is > 0 ? (double)(monthToDate / report.Total.PeriodTarget.Value) : null,
            rows.Sum(item => item.Day?.CustomersVisited ?? 0),
            rows.Sum(item => item.Day?.Kilometres ?? 0),
            rows.Sum(item => item.Day?.AccountSales ?? 0m)
        ]);

        row = MgmtNote(ws, row, lastCol, "* Account sales are keyed in SAP by the office and are not in van sales. Target met is the month to date over the month's target.");
        row++;

        var issues = rows.Where(item => !string.IsNullOrWhiteSpace(item.Day?.Notes)).ToList();
        TsSectionTitle(ws, row, lastCol, issues.Count == 0 ? $"OPERATING ISSUES  |  NONE ENTERED FOR {day:d MMM yyyy}" : "OPERATING ISSUES");
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

        int row = TsTitleBar(ws, $"DAILY TRUCK CARDS  |  {day:dddd d MMMM yyyy}", Math.Max(lastCol, 3), now);

        // A truck's name over its Daily and MTD columns, the workbook's card laid side by side.
        ws.Cell(row, 1).Value = "Metric";
        for (int i = 0; i < rows.Count; i++)
        {
            var col = 2 + i * 2;
            ws.Range(row, col, row, col + 1).Merge();
            ws.Cell(row, col).Value = rows[i].Truck.RouteName;
        }

        var head = ws.Range(row, 1, row, Math.Max(lastCol, 1));
        head.Style.Fill.BackgroundColor = TsNavy;
        head.Style.Font.FontColor = XLColor.White;
        head.Style.Font.Bold = true;
        head.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        row++;

        var headers = new List<string> { string.Empty };
        foreach (var _ in rows)
        {
            headers.Add("Daily");
            headers.Add("MTD");
        }

        row = TsColumnHeaders(ws, row, lastCol, headers.ToArray());
        ws.Row(row - 1).Height = 18;

        var metrics = new List<(string Label, Func<VanManagementTruckDay, object?> Daily, Func<VanManagementTruckDay, object?> ToDate, string? Format)>
        {
            ("Area worked", item => item.Day?.AreaName, _ => null, null),
            ("Customers", item => item.Day?.HasActivity == true ? item.Day.CustomersVisited : null, item => item.ToDate.CustomersVisited, FormatCount),
            ("Departure time", item => item.Day?.TimeOut?.TimeOfDay, _ => null, FormatClock),
            ("Return time", item => item.Day?.ReturnedLate == true ? null : item.Day?.TimeIn?.TimeOfDay, _ => null, FormatClock),
            ("CCR", item => item.Day?.CallComplianceRate, item => item.ToDate.CallComplianceRate, FormatPercent),
            ("PCR", item => item.Day?.ProductiveCallRate, item => item.ToDate.ProductiveCallRate, FormatPercent),
            ("Average order", item => item.Day?.AverageOrderValue, item => item.ToDate.AverageOrderValue, FormatUsd),
            ("Actual day sales", item => item.Day?.HasActivity == true ? item.Day.Sales : null, item => item.ToDate.Sales, FormatUsd),
            ("New customers", item => item.Day?.HasActivity == true ? item.Day.NewCustomers : null, item => item.ToDate.NewCustomers, FormatCount),
            ("Ecocash", item => item.Day?.HasActivity == true ? item.Day.Ecocash : null, item => item.ToDate.Ecocash, FormatUsd),
            ("Account customers", item => item.Day?.AccountSales, item => item.ToDate.AccountSales, FormatUsd),
            ("Opening odometer (km)", item => item.Day?.StartingMileage, _ => null, FormatCount),
            ("Closing odometer (km)", item => item.Day?.ClosingMileage, _ => null, FormatCount),
            ("Total km travelled", item => item.Day?.Kilometres, item => item.ToDate.Kilometres, FormatCount),
            ("Fuel allocated (L)", item => item.Day?.FuelLitres, item => item.ToDate.FuelLitres > 0 ? item.ToDate.FuelLitres : null, FormatLitres),
            ("Truck target", item => item.Day?.DailySalesTarget, item => item.ToDate.PeriodTarget, FormatUsd),
            ("Balance to target", _ => null, item => item.ToDate.BalanceToTarget, FormatUsdSigned)
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

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeCol: 1);
        ws.Column(1).Width = 24;
    }

    // — Weekly Sales ————————————————————————————————————————————————————————————————————————————

    private static void BuildMgmtWeeklySheet(XLWorkbook workbook, VanManagementReport report, DateTime? weekStart, DateTime now)
    {
        const int lastCol = 6;
        var ws = workbook.Worksheets.Add("Weekly Sales");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"WEEKLY VAN SALES  |  {MgmtPeriod(report).ToUpperInvariant()}", lastCol, now);
        var week = VanManagementReportBuilder.WeekOf(report, weekStart);

        if (week is not null)
        {
            row = TsKpiStrip(ws, row, lastCol,
                ("Week", week.Label, null),
                ("Weekly Van Sales", TruckDollars(week.Sales), null),
                ("Truck-Days Reported", week.Submissions.ToString("N0"), null),
                ("Trucks Reporting", $"{week.TrucksReporting} / {week.Trucks.Count}", null),
                ("Account Deliveries", TruckDollars(week.AccountSales), null));

            TsSectionTitle(ws, row, lastCol, "SELECTED WEEK  |  TRUCK DETAIL");
            row += 2;
            row = MgmtTable(ws, row, lastCol, week.Trucks.OrderByDescending(truck => truck.Sales).ToList(),
            [
                new("Truck", truck => truck.RouteName),
                new("Weekly Sales", truck => truck.Sales, FormatUsd),
                new("Days Reported", truck => truck.Submissions, FormatCount),
                new("Share Of Week", truck => truck.Share, FormatPercent)
            ], total: ["TOTAL", week.Sales, week.Submissions, week.Sales > 0 ? 1.0 : null]);
            row++;

            TsSectionTitle(ws, row, lastCol, "DAILY PATTERN");
            row += 2;
            row = MgmtTable(ws, row, lastCol, week.Days.ToList(),
            [
                new("Date", item => item.Date, FormatDayDate),
                new("Van Sales", item => item.Submissions > 0 ? item.Sales : null, FormatUsd),
                new("Trucks Reporting", item => item.Submissions, FormatCount)
            ]);
            row++;
        }

        TsSectionTitle(ws, row, lastCol, "WEEK INDEX  |  THE FIRST AND LAST WEEKS HOLD THIS PERIOD'S DATES ONLY");
        row += 2;
        MgmtTable(ws, row, lastCol, report.Weeks.ToList(),
        [
            new("Week Start", item => item.Start, FormatDayDate),
            new("Week End", item => item.End, FormatDayDate),
            new("Van Sales", item => item.HasData ? item.Sales : null, FormatUsd),
            new("Truck-Days Reported", item => item.Submissions, FormatCount),
            new("Trucks Reporting", item => item.TrucksReporting, FormatCount)
        ]);

        TsFinalize(ws, lastCol);
    }

    // — Route Sales Ranking —————————————————————————————————————————————————————————————————————

    private static void BuildMgmtRoutesSheet(XLWorkbook workbook, VanManagementReport report, DateTime now)
    {
        const int lastCol = 6;
        var ws = workbook.Worksheets.Add("Route Sales Ranking");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"{MgmtPeriod(report).ToUpperInvariant()}  |  ROUTE SALES RANKING", lastCol, now);
        var routes = report.Routes;

        row = TsKpiStrip(ws, row, lastCol,
            ("Highest", routes.Highest is { } high ? $"{high.Area} · {TruckDollars(high.Sales)}" : "—", null),
            ("Middle", routes.Middle is { } middle ? $"{middle.Area} · {TruckDollars(middle.Sales)}" : "—", null),
            ("Lowest", routes.Lowest is { } low ? $"{low.Area} · {TruckDollars(low.Sales)}" : "—", null),
            ("Truck-Days Reported", routes.SalesRecords.ToString("N0"), null),
            ("Distinct Areas", routes.Ranked.Count.ToString("N0"), null),
            ("Days With No Area", routes.RecordsWithoutArea.ToString("N0"), routes.RecordsWithoutArea > 0 ? TsOrange : null));

        row = MgmtNote(ws, row, lastCol,
            "Areas are ranked by the period's van sales. Middle is the upper middle rank when the count is even. An area is named as it was recorded, so spelling variants appear separately; days that named no area are left out.");
        row++;

        MgmtTable(ws, row, lastCol, routes.Ranked.Select((route, rank) => (route, rank)).ToList(),
        [
            new("#", item => item.rank + 1, FormatCount),
            new("Area Worked", item => item.route.Area),
            new("Trucks", item => item.route.Trucks),
            new("Days", item => item.route.Days, FormatCount),
            new("Van Sales (US$)", item => item.route.Sales, FormatUsd),
            new("Per Day", item => item.route.Days > 0 ? item.route.Sales / item.route.Days : null, FormatUsd)
        ]);

        TsFinalize(ws, lastCol, freezeRow: row);
    }

    // — Product group ———————————————————————————————————————————————————————————————————————————

    private static void BuildMgmtProductsSheet(XLWorkbook workbook, VanManagementReport report, DateTime now)
    {
        const int lastCol = 5;
        var name = MgmtSheetName(report.ProductGroupName!);
        var ws = workbook.Worksheets.Add(name);
        TsApplyDefaults(ws);

        var products = report.Products;
        int row = TsTitleBar(ws, $"{report.ProductGroupName!.ToUpperInvariant()}  |  UNITS AND SALES VALUE", lastCol, now);

        row = TsKpiStrip(ws, row, lastCol,
            ("Units", products.Units.ToString("N0"), null),
            ("Value With VAT", TruckDollars(products.Value), null),
            ("Share Of Van Sales", RateText(report.Total.Sales > 0 ? (double)(products.Value / report.Total.Sales) : null), null),
            ("Units On No Route", products.UnitsOutsideRoutes.ToString("N0"), products.UnitsOutsideRoutes > 0 ? TsOrange : null));

        row = MgmtNote(ws, row, lastCol, "Counted from the van invoices' lines in the unit each was sold in. Value is US dollars with VAT, the same money as van sales.");
        row++;

        TsSectionTitle(ws, row, lastCol, "BY TRUCK");
        row += 2;
        row = MgmtTable(ws, row, lastCol, products.Trucks.OrderByDescending(truck => truck.Units).ToList(),
        [
            new("Truck", truck => truck.RouteName),
            new("Units", truck => truck.Units, FormatQuantity),
            new("Value With VAT", truck => truck.Value, FormatUsd)
        ], total: ["ALL TRUCKS", products.Units, products.Value]);
        row++;

        TsSectionTitle(ws, row, lastCol, "BY ITEM");
        row += 2;
        row = MgmtTable(ws, row, lastCol, products.Items.ToList(),
        [
            new("Item Code", item => item.ItemCode),
            new("Description", item => item.ItemName),
            new("Units", item => item.Units, FormatQuantity),
            new("Value With VAT", item => item.Value, FormatUsd)
        ]);
        row++;

        TsSectionTitle(ws, row, lastCol, "DAILY UNITS AND VALUE");
        row += 2;
        MgmtTable(ws, row, lastCol, products.Days.ToList(),
        [
            new("Date", item => item.Date, FormatDayDate),
            new("Units", item => item.Units, FormatQuantity),
            new("Value With VAT", item => item.Value, FormatUsd)
        ]);

        TsFinalize(ws, lastCol);
    }

    // — Breakages ———————————————————————————————————————————————————————————————————————————————

    private static void BuildMgmtBreakagesSheet(XLWorkbook workbook, VanManagementReport report, DateTime now)
    {
        const int lastCol = 10;
        var ws = workbook.Worksheets.Add("Breakages");
        TsApplyDefaults(ws);

        var breakages = report.Breakages;
        int row = TsTitleBar(ws, $"VAN SALES  |  MARKET BREAKAGES  —  {MgmtPeriod(report)}", lastCol, now);

        row = TsKpiStrip(ws, row, lastCol,
            ("Reported SKU Lines", breakages.Lines.Count.ToString("N0"), null),
            ("Broken Units", breakages.Units.ToString("N0"), null),
            ("Value Before VAT", TruckDollars(breakages.Value), null),
            ("Lines Not Valued", breakages.Unvalued.ToString("N0"), breakages.Unvalued > 0 ? TsOrange : null));

        row = MgmtNote(ws, row, lastCol,
            "From the handsets' market breakage reports; reports the office rejected are left out. Units are the office's confirmed count where it has counted, else the rep's. " +
            "Value is at the average price before VAT the vans sold the item for in the period, and blank where they sold none of it.");
        row++;

        TsSectionTitle(ws, row, lastCol, "BREAKAGES BY VAN SALES TRUCK");
        row += 2;
        row = MgmtTable(ws, row, lastCol, breakages.Trucks.ToList(),
        [
            new("Truck", truck => truck.RouteName),
            new("Reported SKU Lines", truck => truck.Lines, FormatCount),
            new("Broken Units", truck => truck.Units, FormatQuantity),
            new("Value Before VAT (USD)", truck => truck.Value, FormatUsd)
        ], total: ["TOTAL REPORTED", breakages.Lines.Count, breakages.Units, breakages.Value]);
        row++;

        TsSectionTitle(ws, row, lastCol, "BY CAUSE OF BREAKAGE");
        row += 2;
        row = MgmtTable(ws, row, lastCol, breakages.Causes.ToList(),
        [
            new("Cause", cause => cause.Cause),
            new("Lines", cause => cause.Lines, FormatCount),
            new("Broken Units", cause => cause.Units, FormatQuantity),
            new("Value Before VAT (USD)", cause => cause.Value, FormatUsd)
        ], total: ["ALL CAUSES", breakages.Lines.Count, breakages.Units, breakages.Value]);
        row++;

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
            new("Units", line => line.Units, FormatQuantity),
            new("Value Before VAT", line => line.ValueBeforeVat, FormatUsd),
            new("Status", line => line.IsConfirmed ? line.Status : $"{line.Status} (rep's count)")
        ]);

        TsFinalize(ws, lastCol);
    }

    // — Helpers —————————————————————————————————————————————————————————————————————————————————

    /// <summary>
    /// Writes a header row, a striped row per item and, when given, a total row; returns the row after.
    /// A null value leaves the cell empty, except a rate column, where it is an em dash: nothing under it,
    /// never 0%.
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
                MgmtCell(ws.Cell(row, col), column.Value(item), formatOf?.Invoke(item, col) ?? column.Format);
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

        string[] taken = ["Insights Summary", "Management Overview", "Monthly Summary", "Daily Management View", "Daily Truck Cards", "Weekly Sales", "Route Sales Ranking", "Breakages", "Raw Data", "Trucks", "Days"];
        return taken.Contains(clean, StringComparer.OrdinalIgnoreCase) ? $"{clean[..Math.Min(clean.Length, 27)]} (2)" : clean;
    }
}
