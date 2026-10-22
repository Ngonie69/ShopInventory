using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The Daily Truck Report as a workbook: each truck against its target, and a row per truck per day.
/// </summary>
/// <remarks>
/// This is the office's VANSALES DAILY REPORT workbook going back out the way it came in, so three
/// things differ from the other van workbooks beside it:
///
///  - **Money is a number.** Every figure here is US dollars, so there is no per-currency text to
///    keep apart and a reader can sum and pivot it.
///  - **The Days sheet is one table with a filter.** Its totals are SUBTOTALs, so narrowing it to a
///    truck or a week leaves that truck's or that week's total underneath.
///  - **A blank is "not recorded".** A day nothing was recorded on has empty cells, never zeros, and
///    a rate with nothing under it is an em dash.
///
/// Each sheet is a list of columns, a heading with the cell it writes, so a heading cannot drift off
/// its figure when a column is added in the middle.
/// </remarks>
public partial class ReportExportService
{
    // The balance to target goes negative when a truck has passed it. That is not a loss, so it
    // is not put in red brackets the way FormatUsd would.
    private const string FormatUsdSigned = "$#,##0.00;-$#,##0.00";

    // A tender nobody paid by is a real nought, and is drawn as a dash so the columns that were
    // used stand out. An empty cell is still the only thing that means "not recorded".
    private const string FormatUsdDashZero = "$#,##0.00;[Red]($#,##0.00);\"-\"";
    private const string FormatLitres = "#,##0.00";
    private const string FormatClock = "hh:mm";
    private const string FormatLateReturn = "dd MMM hh:mm";

    /// <summary>One column: its heading, the cell it writes for a row, and the format of its total if it has one.</summary>
    private sealed record TruckReportColumn<TRow>(string Header, Action<IXLCell, TRow> Write, string? TotalFormat = null);

    public byte[] ExportVanDailyTruckReportToExcel(VanDailyTruckReportResponse report)
    {
        using var workbook = NewWorkbook("Van Daily Truck Report");
        var now = DateTime.UtcNow.AddHours(2); // CAT

        BuildVanDailyTruckSummarySheet(workbook, report, now);
        BuildVanDailyTruckDaysSheet(workbook, report, now);

        return WorkbookToBytes(workbook);
    }

    private static readonly TruckReportColumn<VanDailyTruckSummary>[] TruckSummaryColumns =
    [
        new("Truck", (cell, truck) => cell.Value = truck.RouteName),
        new("Code", (cell, truck) => cell.Value = truck.RouteCode),
        new("Territory", (cell, truck) => cell.Value = truck.Territory ?? string.Empty),
        new("Daily Target", (cell, truck) => TruckMoney(cell, truck.DailySalesTarget)),
        new("Period Target", (cell, truck) => TruckMoney(cell, truck.PeriodTarget)),
        new("Sales (USD, with VAT)", (cell, truck) => TruckMoney(cell, truck.Sales)),
        // SAP's booked gross profit on the posted dollar sales; blank where none of them is in SAP yet.
        new("Gross Profit (USD)", (cell, truck) => TruckMoney(cell, truck.GrossProfit, FormatUsdSigned)),
        new("Margin", (cell, truck) => TruckRate(cell, truck.MarginPercent is { } margin ? (double)(margin / 100m) : null)),
        new("Balance To Target", (cell, truck) => TruckMoney(cell, truck.BalanceToTarget, FormatUsdSigned)),
        new("Achieved", (cell, truck) => TruckRate(cell, truck.TargetAchieved)),
        new("Days Reported", (cell, truck) =>
        {
            TruckCount(cell, truck.DaysReported);

            if (truck.DaysReported < truck.WorkingDaysElapsed)
            {
                cell.Style.Font.FontColor = TsRed;
                cell.Style.Font.Bold = true;
            }
        }),
        new("Working Days So Far", (cell, truck) => TruckCount(cell, truck.WorkingDaysElapsed)),
        new("Invoices", (cell, truck) => TruckCount(cell, truck.SalesCount)),
        new("Shops Visited", (cell, truck) => TruckCount(cell, truck.CustomersVisited)),
        new("CCR", (cell, truck) => TruckRate(cell, truck.CallComplianceRate)),
        new("CCR Measured On", (cell, truck) => cell.Value = TruckPlanText(truck)),
        new("Shops Bought", (cell, truck) => TruckCount(cell, truck.ProductiveCalls)),
        new("PCR", (cell, truck) => TruckRate(cell, truck.ProductiveCallRate)),
        new("New Customers", (cell, truck) => TruckCount(cell, truck.NewCustomers)),
        new("Average Order", (cell, truck) => TruckMoney(cell, truck.AverageOrderValue)),
        new("Ecocash", (cell, truck) => TruckMoney(cell, truck.Ecocash, FormatUsdDashZero)),
        new("Account Sales (not in Sales)", (cell, truck) => TruckMoney(cell, truck.AccountSales > 0 ? truck.AccountSales : null)),
        new("Km", (cell, truck) => TruckCount(cell, truck.Kilometres > 0 ? truck.Kilometres : null)),
        new("Fuel Issued (L)", (cell, truck) => TruckNumber(cell, truck.FuelLitres > 0 ? truck.FuelLitres : null, FormatLitres)),
        new("Km A Litre", (cell, truck) => TruckNumber(cell, truck.KilometresPerLitre, FormatQuantity)),
        new("Sales A Km", (cell, truck) => TruckMoney(cell, truck.SalesPerKilometre))
    ];

    private static readonly TruckReportColumn<VanDailyTruckDay>[] TruckDayColumns =
    [
        new("Date", (cell, day) =>
        {
            cell.Value = day.TradingDate.Date;
            cell.Style.NumberFormat.Format = FormatDayDate;
        }),
        new("Truck", (cell, day) => cell.Value = day.RouteName),
        new("Code", (cell, day) => cell.Value = day.RouteCode),
        new("Territory", (cell, day) => cell.Value = day.Territory ?? string.Empty),
        new("Area Worked", (cell, day) => cell.Value = day.AreaName ?? string.Empty),
        new("Area From", (cell, day) => cell.Value = TruckAreaSourceText(day.AreaSource)),
        new("Time Out", (cell, day) =>
        {
            if (day.TimeOut is { } timeOut)
            {
                cell.Value = timeOut.TimeOfDay;
                cell.Style.NumberFormat.Format = FormatClock;
            }
        }),
        new("Time In", (cell, day) =>
        {
            if (day.TimeIn is not { } timeIn)
            {
                return;
            }

            // Closed on a later date: this is when End Day was tapped, not when the truck got
            // back, so it carries its date and cannot be read as a time on the trading day.
            if (day.ReturnedLate)
            {
                cell.Value = timeIn;
                cell.Style.NumberFormat.Format = FormatLateReturn;
                cell.Style.Font.FontColor = TsOrange;
            }
            else
            {
                cell.Value = timeIn.TimeOfDay;
                cell.Style.NumberFormat.Format = FormatClock;
            }
        }),
        new("Shops On Plan", (cell, day) => TruckCount(cell, day.PlannedCustomers)),
        new("Planned Calls Made", (cell, day) => TruckCount(cell, day.PlannedCustomers is > 0 ? day.PlannedCallsMade : null)),
        new("CCR", (cell, day) =>
        {
            // No plan at all leaves the cell empty; the dash is for a plan nobody could call on.
            if (day.PlannedCustomers.HasValue)
            {
                TruckRate(cell, day.CallComplianceRate);
            }
        }),
        new("CCR Measured On", (cell, day) => cell.Value = TruckPlanText(day)),
        new("Shops Visited", (cell, day) => TruckCount(cell, Recorded(day, day.CustomersVisited)), FormatCount),
        new("Shops Bought", (cell, day) => TruckCount(cell, Recorded(day, day.ProductiveCalls)), FormatCount),
        new("PCR", (cell, day) =>
        {
            if (day.HasActivity)
            {
                TruckRate(cell, day.ProductiveCallRate);
            }
        }),
        new("New Customers", (cell, day) => TruckCount(cell, Recorded(day, day.NewCustomers)), FormatCount),
        new("Invoices", (cell, day) => TruckCount(cell, Recorded(day, day.SalesCount)), FormatCount),
        new("Sales (USD, with VAT)", (cell, day) => TruckMoney(cell, Recorded(day, day.Sales)), FormatUsd),
        new("Gross Profit (USD)", (cell, day) => TruckMoney(cell, day.GrossProfit, FormatUsdSigned), FormatUsdSigned),
        new("Margin", (cell, day) => TruckRate(cell, day.MarginPercent is { } margin ? (double)(margin / 100m) : null)),
        new("Cash", (cell, day) => TruckMoney(cell, Recorded(day, day.Cash), FormatUsdDashZero), FormatUsdDashZero),
        new("Ecocash", (cell, day) => TruckMoney(cell, Recorded(day, day.Ecocash), FormatUsdDashZero), FormatUsdDashZero),
        new("Innbucks", (cell, day) => TruckMoney(cell, Recorded(day, day.Innbucks), FormatUsdDashZero), FormatUsdDashZero),
        new("Other Tender", (cell, day) => TruckMoney(cell, Recorded(day, day.OtherTender), FormatUsdDashZero), FormatUsdDashZero),
        new("No Tender Recorded", (cell, day) => TruckMoney(cell, Recorded(day, day.Untendered), FormatUsdDashZero), FormatUsdDashZero),
        new("Account Sales (not in Sales)", (cell, day) => TruckMoney(cell, day.AccountSales), FormatUsd),
        new("Other Currency Takings (not in Sales)", (cell, day) => TruckNumber(cell, day.OtherCurrencySales, FormatMoney)),
        new("Declared At End Day", (cell, day) => TruckMoney(cell, day.DeclaredTotal), FormatUsd),
        new("Day Target", (cell, day) => TruckMoney(cell, day.DailySalesTarget)),
        new("Sales Against Target", (cell, day) =>
            // Today is not over, so it is not held against a target it still has hours to meet.
            TruckMoney(cell, day.IsComplete && day.DailySalesTarget is { } target ? day.Sales - target : null)),
        new("Odometer Out", (cell, day) => TruckCount(cell, day.StartingMileage)),
        new("Odometer In", (cell, day) => TruckCount(cell, day.ClosingMileage)),
        new("Odometer Reads", (cell, day) =>
            cell.Value = day.StartingMileage.HasValue || day.ClosingMileage.HasValue
                ? VanOdometer.ReadsMiles(day.OdometerUnit) ? "miles" : "km"
                : string.Empty),
        new("Km", (cell, day) => TruckCount(cell, day.Kilometres), FormatCount),
        new("Fuel Issued (L)", (cell, day) => TruckNumber(cell, day.FuelLitres, FormatLitres), FormatLitres),
        new("Flags", (cell, day) =>
        {
            cell.Value = TruckDayFlags(day);
            cell.Style.Font.FontColor = TsRed;
        }),
        new("Office Note", (cell, day) => cell.Value = day.Notes ?? string.Empty)
    ];

    private static void BuildVanDailyTruckSummarySheet(
        XLWorkbook workbook,
        VanDailyTruckReportResponse report,
        DateTime now)
    {
        var columns = TruckSummaryColumns;
        int lastCol = columns.Length;
        var ws = workbook.Worksheets.Add("Trucks");
        TsApplyDefaults(ws);

        var period = $"VAN DAILY TRUCK REPORT  —  {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}";
        int row = TsTitleBar(ws, period, lastCol, now);

        var total = report.Total;

        row = TsKpiStrip(ws, row, lastCol,
            ("Period Target", TruckDollars(total.PeriodTarget), null),
            ("Sales To Date", TruckDollars(total.Sales), null),
            ("Balance To Target", TruckDollars(total.BalanceToTarget), null),
            ("Achieved", RateText(total.TargetAchieved), null),
            ("Working Days", $"{report.WorkingDaysElapsed:N0} of {report.WorkingDays:N0}", null),
            ("Shops Visited", total.CustomersVisited.ToString("N0"), null),
            ("CCR", RateText(total.CallComplianceRate), null),
            ("PCR", RateText(total.ProductiveCallRate), null),
            ("Account Sales", total.AccountSales > 0 ? TruckDollars(total.AccountSales) : "—", null),
            ("Kilometres", total.Kilometres > 0 ? total.Kilometres.ToString("N0") : "—", null),
            ("Litres Issued", total.FuelLitres > 0 ? total.FuelLitres.ToString("N0") : "—", null),
            ("Km A Litre", total.KilometresPerLitre is { } perLitre ? perLitre.ToString("N2") : "—", null));

        // What the figures are, before any of them: a forwarded workbook reaches people who never
        // saw the page.
        TsSectionTitle(ws, row, lastCol, "HOW TO READ THIS");
        row += 2;

        foreach (var note in TruckReportNotes(report))
        {
            ws.Cell(row, 1).Value = note;
            ws.Range(row, 1, row, lastCol).Merge();
            ws.Cell(row, 1).Style.Font.FontSize = 9;
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
            row++;
        }

        if (report.SalesOutsideRoutes > 0)
        {
            ws.Cell(row, 1).Value =
                $"{TruckDollars(report.SalesOutsideRoutes)} was sold by logins that are on no route, and is in none of the rows below.";
            ws.Range(row, 1, row, lastCol).Merge();
            ws.Cell(row, 1).Style.Font.FontSize = 9;
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
            row++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "TRUCK-DAYS TO LOOK AT — FILTER THE FLAGS COLUMN ON THE DAYS SHEET");
        row += 2;

        var exceptions = report.Exceptions;
        row = TsKpiStrip(ws, row, lastCol,
            ("Not Reported", exceptions.NotReported.ToString("N0"), TruckAlert(exceptions.NotReported)),
            ("Below Target", exceptions.BelowTarget.ToString("N0"), TruckAlert(exceptions.BelowTarget)),
            ("Distance, No Sales", exceptions.DistanceWithoutSales.ToString("N0"), TruckAlert(exceptions.DistanceWithoutSales)),
            ("Mileage To Check", exceptions.MileageNeedsReview.ToString("N0"), TruckAlert(exceptions.MileageNeedsReview)),
            ("Closed A Day Late", exceptions.ReturnedLate.ToString("N0"), TruckAlert(exceptions.ReturnedLate)));

        TsSectionTitle(ws, row, lastCol, "EACH TRUCK AGAINST ITS TARGET");
        row += 2;
        row = TsColumnHeaders(ws, row, lastCol, columns.Select(column => column.Header).ToArray());

        int index = 0;
        foreach (var truck in report.Trucks)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            WriteTruckReportRow(ws, row, columns, truck);
            row++;
            index++;
        }

        if (report.Trucks.Count > 0)
        {
            // The fleet's row is the API's own, not a sum down the sheet: its rates are over the
            // fleet's calls and cannot be added up from the trucks'.
            WriteTruckReportRow(ws, row, columns, total);
            ws.Cell(row, 1).Value = "ALL TRUCKS";
            ws.Cell(row, 2).Value = string.Empty;
            ws.Cell(row, 3).Value = string.Empty;
            TsSummaryRow(ws, row, lastCol);
        }

        TsFinalize(ws, lastCol, freezeRow: 1, freezeCol: 1);
    }

    private static void BuildVanDailyTruckDaysSheet(
        XLWorkbook workbook,
        VanDailyTruckReportResponse report,
        DateTime now)
    {
        var columns = TruckDayColumns;
        int lastCol = columns.Length;
        var ws = workbook.Worksheets.Add("Days");
        TsApplyDefaults(ws);

        int headerRow = TsTitleBar(
            ws,
            $"EVERY TRUCK, EVERY DAY  —  {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}",
            lastCol,
            now);

        int row = TsColumnHeaders(ws, headerRow, lastCol, columns.Select(column => column.Header).ToArray());
        int firstDataRow = row;

        int index = 0;
        foreach (var day in report.Days.OrderBy(day => day.TradingDate).ThenBy(day => day.Territory).ThenBy(day => day.RouteName))
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            WriteTruckReportRow(ws, row, columns, day);

            // A day with nothing on it is greyed, so a truck's silent days stand apart from its
            // working ones without being left out.
            if (!day.HasActivity)
            {
                ws.Range(row, 1, row, lastCol - 2).Style.Font.FontColor = TsTextMuted;
            }

            row++;
            index++;
        }

        int lastDataRow = row - 1;

        if (lastDataRow >= firstDataRow)
        {
            ws.Range(headerRow, 1, lastDataRow, lastCol).SetAutoFilter();

            ws.Cell(row, 1).Value = "TOTAL OF THE ROWS SHOWN";

            for (int col = 1; col <= lastCol; col++)
            {
                if (columns[col - 1].TotalFormat is { } format)
                {
                    WriteSubtotal(ws, row, col, firstDataRow, lastDataRow, format);
                }
            }

            TsSummaryRow(ws, row, lastCol);
        }

        TsFinalize(ws, lastCol, freezeRow: headerRow, freezeCol: 2);
    }

    private static void WriteTruckReportRow<TRow>(
        IXLWorksheet ws,
        int row,
        TruckReportColumn<TRow>[] columns,
        TRow item)
    {
        for (int col = 1; col <= columns.Length; col++)
        {
            columns[col - 1].Write(ws.Cell(row, col), item);
        }
    }

    private static IEnumerable<string> TruckReportNotes(VanDailyTruckReportResponse report)
    {
        yield return "Sales are US dollars with VAT, counted from the van invoices. A truck's period target is its daily target for every Monday to Friday in the period.";
        yield return "Account sales are the invoices keyed into SAP for account customers on the round. The office enters them, and they are not in the Sales column.";
        yield return "CCR is the planned calls made over the shops on each day's round: those scheduled for the weekday or called on before in the same area.";

        if (report.Trucks.Any(truck => truck.PlanIsWholeList || truck.DaysOnWholeList > 0))
        {
            yield return "Where no round is known the CCR is against the truck's whole customer list and reads low. The CCR Measured On column says which, and a period with any rounds leaves its list days out.";
        }

        yield return "PCR is the shops that bought over the shops called on.";
        yield return report.Days.Any(day => VanOdometer.ReadsMiles(day.OdometerUnit) && day.Kilometres.HasValue)
            ? "Km is kilometres on every truck. Some odometers count in miles: the readings on the Days sheet are as the odometer shows them, in the unit beside them."
            : "Km is the distance between the odometer readings taken at Start Day and End Day.";
        yield return "Fuel is what the office entered as issued. An empty cell is a figure nobody recorded, not a zero.";
    }

    /// <summary>The automatic figures of a day nothing was recorded on are left empty rather than written as zero.</summary>
    private static int? Recorded(VanDailyTruckDay day, int value) => day.HasActivity ? value : null;

    private static decimal? Recorded(VanDailyTruckDay day, decimal value) => day.HasActivity ? value : null;

    private static void TruckMoney(IXLCell cell, decimal? amount, string format = FormatUsd) =>
        TruckNumber(cell, amount, format);

    private static void TruckNumber(IXLCell cell, decimal? amount, string format)
    {
        if (amount is { } value)
        {
            cell.Value = value;
            cell.Style.NumberFormat.Format = format;
        }
    }

    private static void TruckCount(IXLCell cell, int? count)
    {
        if (count is { } value)
        {
            cell.Value = value;
            cell.Style.NumberFormat.Format = FormatCount;
        }
    }

    /// <summary>A real percentage where there is one, so the column sorts; an em dash where nothing was under it. Never 0%.</summary>
    private static void TruckRate(IXLCell cell, double? rate)
    {
        if (rate is { } value)
        {
            cell.Value = value;
            cell.Style.NumberFormat.Format = FormatPercent;
        }
        else
        {
            cell.Value = "—";
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
    }

    private static string TruckDollars(decimal? amount) => amount is { } value ? $"${value:N0}" : "—";

    private static XLColor? TruckAlert(int count) => count > 0 ? TsRed : null;

    private static string TruckPlanText(VanDailyTruckSummary truck)
    {
        if (truck.PlannedCustomers <= 0)
        {
            return "—";
        }

        if (truck.PlanIsWholeList)
        {
            return "Whole list";
        }

        return truck.DaysOnWholeList > 0
            ? $"Rounds ({truck.DaysOnWholeList} list day{(truck.DaysOnWholeList == 1 ? "" : "s")} left out)"
            : "Rounds";
    }

    private static string TruckPlanText(VanDailyTruckDay day) =>
        day.PlannedCustomers is null ? string.Empty
        : day.PlanIsWholeList ? "Whole list"
        : "Round";

    private static string TruckAreaSourceText(string? source) => source switch
    {
        "Office" => "Office entry",
        "Day" => "Start Day",
        "Plan" => "Weekday plan",
        _ => string.Empty
    };

    /// <summary>
    /// Every check a day was picked out by, worded as the counts on the Trucks sheet are, so
    /// filtering this column for one of them lists the days behind its count.
    /// </summary>
    private static string TruckDayFlags(VanDailyTruckDay day)
    {
        var flags = new List<string>();

        if (day.NotReported) flags.Add("not reported");
        if (day.BelowTarget) flags.Add("below target");
        if (day.DistanceWithoutSales) flags.Add("distance, no sales");
        if (day.MileageNeedsReview) flags.Add("mileage to check");
        if (day.ReturnedLate) flags.Add("closed a day late");
        if (!day.IsWorkingDay) flags.Add("weekend");
        if (!day.IsComplete) flags.Add("today");

        return string.Join("; ", flags);
    }
}
