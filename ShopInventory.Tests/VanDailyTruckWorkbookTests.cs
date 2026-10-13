using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Daily Truck Report workbook, read back out of the bytes the page downloads.
/// </summary>
/// <remarks>
/// Every figure is looked up by its column heading rather than its position, so these tests say what
/// a reader sees under each heading and keep passing when a column is added.
/// </remarks>
public class VanDailyTruckWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    /// <summary>The row holding the table's headings: the one whose first cell reads <paramref name="firstHeading"/>.</summary>
    private static int HeadingRow(IXLWorksheet sheet, string firstHeading) =>
        sheet.Column(1).CellsUsed().Single(cell => cell.GetString() == firstHeading).Address.RowNumber;

    private static int Column(IXLWorksheet sheet, int headingRow, string heading) =>
        sheet.Row(headingRow).CellsUsed().Single(cell => cell.GetString() == heading).Address.ColumnNumber;

    /// <summary>The first row under the headings whose cell under <paramref name="heading"/> reads <paramref name="value"/>.</summary>
    private static int RowWhere(IXLWorksheet sheet, int headingRow, string heading, string value)
    {
        int column = Column(sheet, headingRow, heading);

        return sheet.Column(column).CellsUsed()
            .First(cell => cell.Address.RowNumber > headingRow && cell.GetString() == value)
            .Address.RowNumber;
    }

    private static IXLCell Under(IXLWorksheet sheet, int headingRow, int row, string heading) =>
        sheet.Cell(row, Column(sheet, headingRow, heading));

    [Fact]
    public void The_workbook_has_the_trucks_and_then_their_days()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));

        Assert.Equal(["Trucks", "Days"], workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
        Assert.Contains("VAN DAILY TRUCK REPORT  —  01 Oct 2026 to 08 Oct 2026", TextOf(workbook.Worksheet("Trucks")));
    }

    [Fact]
    public void An_empty_period_still_produces_a_readable_workbook()
    {
        var bytes = _service.ExportVanDailyTruckReportToExcel(new VanDailyTruckReportResponse
        {
            FromDate = new DateTime(2026, 10, 1),
            ToDate = new DateTime(2026, 10, 31)
        });

        using var workbook = Open(bytes);
        var days = workbook.Worksheet("Days");

        Assert.Equal(2, workbook.Worksheets.Count);
        Assert.Equal("Date", days.Cell(2, 1).GetString());
        Assert.False(days.AutoFilter.IsEnabled, "a filter was put over a table with no rows");
        Assert.DoesNotContain("ALL TRUCKS", TextOf(workbook.Worksheet("Trucks")));
    }

    [Fact]
    public void A_truck_row_carries_its_money_as_numbers_under_the_right_headings()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Trucks");
        int headings = HeadingRow(sheet, "Truck");
        int west = RowWhere(sheet, headings, "Truck", "West 1");

        Assert.Equal(15000m, Under(sheet, headings, west, "Period Target").GetValue<decimal>());
        Assert.Equal(9240.50m, Under(sheet, headings, west, "Sales (USD, with VAT)").GetValue<decimal>());
        Assert.Equal(5759.50m, Under(sheet, headings, west, "Balance To Target").GetValue<decimal>());
        Assert.Equal(XLDataType.Number, Under(sheet, headings, west, "Achieved").DataType);
        Assert.Equal("61.6%", Under(sheet, headings, west, "Achieved").GetFormattedString());
        Assert.Equal("90.0%", Under(sheet, headings, west, "CCR").GetFormattedString());
        Assert.Equal("Rounds (1 list day left out)", Under(sheet, headings, west, "CCR Measured On").GetString());
        Assert.Equal(412, Under(sheet, headings, west, "Km").GetValue<int>());
        Assert.Equal(6.87m, Under(sheet, headings, west, "Km A Litre").GetValue<decimal>());
        Assert.Equal(320m, Under(sheet, headings, west, "Account Sales (not in Sales)").GetValue<decimal>());
    }

    [Fact]
    public void A_truck_with_no_target_no_plan_and_no_fuel_shows_gaps_rather_than_zeros()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Trucks");
        int headings = HeadingRow(sheet, "Truck");
        int upcountry = RowWhere(sheet, headings, "Truck", "Upcountry 2");

        Assert.True(Under(sheet, headings, upcountry, "Period Target").IsEmpty());
        Assert.True(Under(sheet, headings, upcountry, "Balance To Target").IsEmpty());
        Assert.Equal("—", Under(sheet, headings, upcountry, "Achieved").GetString());
        Assert.Equal("—", Under(sheet, headings, upcountry, "CCR").GetString());
        Assert.Equal("—", Under(sheet, headings, upcountry, "CCR Measured On").GetString());
        Assert.True(Under(sheet, headings, upcountry, "Fuel Issued (L)").IsEmpty());
        Assert.True(Under(sheet, headings, upcountry, "Km A Litre").IsEmpty());
        Assert.True(Under(sheet, headings, upcountry, "Account Sales (not in Sales)").IsEmpty());
    }

    [Fact]
    public void The_fleet_row_is_the_reports_own_total_not_a_sum_down_the_sheet()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Trucks");
        int headings = HeadingRow(sheet, "Truck");
        int fleet = RowWhere(sheet, headings, "Truck", "ALL TRUCKS");

        Assert.Equal(10140.50m, Under(sheet, headings, fleet, "Sales (USD, with VAT)").GetValue<decimal>());

        // 54 planned calls made of 60 on the rounds worked: the fleet's own rate, which the two
        // trucks' rates (90% and none) could not be added up into.
        Assert.Equal("90.0%", Under(sheet, headings, fleet, "CCR").GetFormattedString());
        Assert.False(Under(sheet, headings, fleet, "Sales (USD, with VAT)").HasFormula);
        Assert.True(Under(sheet, headings, fleet, "Code").IsEmpty());
    }

    [Fact]
    public void The_notes_say_what_the_figures_are_before_any_of_them()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Trucks");
        var text = TextOf(sheet);

        Assert.Contains("they are not in the Sales column", text);
        Assert.Contains("reads low", text);
        Assert.Contains("Some odometers count in miles", text);
        Assert.Contains("$412 was sold by logins that are on no route", text);

        int notes = HeadingRow(sheet, "HOW TO READ THIS");
        Assert.True(notes < HeadingRow(sheet, "Truck"), "the table comes before the notes that explain it");
    }

    [Fact]
    public void A_period_with_nothing_outside_its_routes_and_no_miles_says_neither()
    {
        var report = Report();
        report.SalesOutsideRoutes = 0;
        report.Days.ForEach(day => day.OdometerUnit = VanOdometer.Kilometres);

        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(report));
        var text = TextOf(workbook.Worksheet("Trucks"));

        Assert.DoesNotContain("on no route", text);
        Assert.DoesNotContain("count in miles", text);
    }

    [Fact]
    public void The_exception_counts_are_on_the_trucks_sheet()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Trucks");

        var label = sheet.CellsUsed().Single(cell => cell.GetString() == "Not Reported");
        Assert.Equal("1", sheet.Cell(label.Address.RowNumber - 1, label.Address.ColumnNumber).GetString());
    }

    [Fact]
    public void A_worked_day_reads_across_under_its_headings()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Days");
        const int headings = 2;
        int row = RowWhere(sheet, headings, "Area Worked", "Glen Norah");

        Assert.Equal(new DateTime(2026, 10, 1), Under(sheet, headings, row, "Date").GetDateTime());
        Assert.Equal("West 1", Under(sheet, headings, row, "Truck").GetString());
        Assert.Equal("Weekday plan", Under(sheet, headings, row, "Area From").GetString());
        Assert.Equal("07:42", Under(sheet, headings, row, "Time Out").GetFormattedString());
        Assert.Equal("16:55", Under(sheet, headings, row, "Time In").GetFormattedString());
        Assert.Equal(60, Under(sheet, headings, row, "Shops On Plan").GetValue<int>());
        Assert.Equal(54, Under(sheet, headings, row, "Planned Calls Made").GetValue<int>());
        Assert.Equal("90.0%", Under(sheet, headings, row, "CCR").GetFormattedString());
        Assert.Equal("Round", Under(sheet, headings, row, "CCR Measured On").GetString());
        Assert.Equal(4620.25m, Under(sheet, headings, row, "Sales (USD, with VAT)").GetValue<decimal>());
        Assert.Equal(4100m, Under(sheet, headings, row, "Cash").GetValue<decimal>());
        Assert.Equal(520.25m, Under(sheet, headings, row, "Ecocash").GetValue<decimal>());

        // A tender nobody paid by is a nought, drawn as a dash. It is not left empty: empty means not recorded.
        Assert.Equal(0m, Under(sheet, headings, row, "Innbucks").GetValue<decimal>());
        Assert.Equal("-", Under(sheet, headings, row, "Innbucks").GetFormattedString());
        Assert.Equal("60.00", Under(sheet, headings, row, "Fuel Issued (L)").GetFormattedString());

        Assert.Equal(320m, Under(sheet, headings, row, "Account Sales (not in Sales)").GetValue<decimal>());
        Assert.Equal(1620.25m, Under(sheet, headings, row, "Sales Against Target").GetValue<decimal>());
        Assert.Equal(string.Empty, Under(sheet, headings, row, "Flags").GetString());
    }

    [Fact]
    public void A_mile_odometer_keeps_its_readings_and_gives_its_distance_in_kilometres()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Days");
        const int headings = 2;
        int row = RowWhere(sheet, headings, "Area Worked", "Glen Norah");

        Assert.Equal(88100, Under(sheet, headings, row, "Odometer Out").GetValue<int>());
        Assert.Equal(88228, Under(sheet, headings, row, "Odometer In").GetValue<int>());
        Assert.Equal("miles", Under(sheet, headings, row, "Odometer Reads").GetString());
        Assert.Equal(206, Under(sheet, headings, row, "Km").GetValue<int>());
    }

    [Fact]
    public void A_day_nothing_was_recorded_on_is_listed_empty_and_flagged()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Days");
        const int headings = 2;
        int row = RowWhere(sheet, headings, "Flags", "not reported; below target");

        Assert.Equal("West 1", Under(sheet, headings, row, "Truck").GetString());
        Assert.True(Under(sheet, headings, row, "Sales (USD, with VAT)").IsEmpty(), "a silent day was given sales of zero");
        Assert.True(Under(sheet, headings, row, "Shops Visited").IsEmpty());
        Assert.True(Under(sheet, headings, row, "PCR").IsEmpty());
        Assert.True(Under(sheet, headings, row, "Time Out").IsEmpty());
        Assert.True(Under(sheet, headings, row, "Km").IsEmpty());
        Assert.Equal(string.Empty, Under(sheet, headings, row, "Odometer Reads").GetString());

        // The target it missed is still there, so the day can be seen to have had one.
        Assert.Equal(3000m, Under(sheet, headings, row, "Day Target").GetValue<decimal>());
        Assert.Equal(-3000m, Under(sheet, headings, row, "Sales Against Target").GetValue<decimal>());
    }

    [Fact]
    public void A_day_closed_late_shows_when_it_was_closed_with_its_date()
    {
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(Report()));
        var sheet = workbook.Worksheet("Days");
        const int headings = 2;
        int row = RowWhere(sheet, headings, "Flags", "closed a day late");

        Assert.Equal("03 Oct 06:10", Under(sheet, headings, row, "Time In").GetFormattedString());
        Assert.Equal("Whole list", Under(sheet, headings, row, "CCR Measured On").GetString());
    }

    [Fact]
    public void Today_is_not_held_against_its_target_or_for_an_odometer_not_yet_closed()
    {
        var report = Report();
        report.Days[^1].DailySalesTarget = 2500m;

        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(report));
        var sheet = workbook.Worksheet("Days");
        const int headings = 2;
        int row = RowWhere(sheet, headings, "Flags", "today");

        Assert.Equal(900m, Under(sheet, headings, row, "Sales (USD, with VAT)").GetValue<decimal>());
        Assert.Equal(2500m, Under(sheet, headings, row, "Day Target").GetValue<decimal>());
        Assert.True(Under(sheet, headings, row, "Sales Against Target").IsEmpty());
        Assert.Equal(120400, Under(sheet, headings, row, "Odometer Out").GetValue<int>());
        Assert.True(Under(sheet, headings, row, "Odometer In").IsEmpty());
    }

    [Fact]
    public void The_flags_column_lists_the_days_behind_each_exception_count()
    {
        var report = Report();
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(report));
        var sheet = workbook.Worksheet("Days");
        const int headings = 2;
        int flags = Column(sheet, headings, "Flags");

        int Flagged(string flag) => sheet.Column(flags).CellsUsed()
            .Count(cell => cell.Address.RowNumber > headings && cell.GetString().Split("; ").Contains(flag));

        Assert.Equal(report.Days.Count(day => day.NotReported), Flagged("not reported"));
        Assert.Equal(report.Days.Count(day => day.BelowTarget), Flagged("below target"));
        Assert.Equal(report.Days.Count(day => day.ReturnedLate), Flagged("closed a day late"));
        Assert.Equal(1, Flagged("not reported"));
        Assert.Equal(report.Exceptions.BelowTarget, Flagged("below target"));
    }

    [Fact]
    public void The_days_are_one_filterable_table_whose_totals_follow_the_filter()
    {
        var report = Report();
        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(report));
        var sheet = workbook.Worksheet("Days");
        const int headings = 2;
        int last = headings + report.Days.Count;

        Assert.True(sheet.AutoFilter.IsEnabled, "the days have no filter dropdowns");
        Assert.Equal(headings, sheet.AutoFilter.Range.FirstRow().RowNumber());
        Assert.Equal(last, sheet.AutoFilter.Range.LastRow().RowNumber());

        var salesTotal = Under(sheet, headings, last + 1, "Sales (USD, with VAT)");

        // SUBTOTAL, not SUM: narrowing the table to one truck has to move the total with it.
        Assert.Contains("SUBTOTAL(109", salesTotal.FormulaA1);
        Assert.Equal(report.Days.Where(day => day.HasActivity).Sum(day => day.Sales), salesTotal.GetValue<decimal>());

        // A target is not a figure to add across trucks that have none, and a rate is not one at all.
        Assert.False(Under(sheet, headings, last + 1, "Day Target").HasFormula);
        Assert.False(Under(sheet, headings, last + 1, "CCR").HasFormula);
    }

    [Fact]
    public void The_days_run_in_date_order_as_the_offices_sheet_did()
    {
        var report = Report();
        report.Days.Reverse();

        using var workbook = Open(_service.ExportVanDailyTruckReportToExcel(report));
        var sheet = workbook.Worksheet("Days");

        var dates = Enumerable.Range(3, report.Days.Count)
            .Select(row => sheet.Cell(row, 1).GetDateTime())
            .ToList();

        Assert.Equal(dates.OrderBy(date => date).ToList(), dates);
    }

    /// <summary>
    /// Two trucks over 1 to 8 October 2026. West 1 reads miles, has a target and one round day; its
    /// Friday was closed the next morning and measured against the list, and its Monday was never
    /// reported. Upcountry 2 has no target, no plan and no office figures, and is out today.
    /// </summary>
    private static VanDailyTruckReportResponse Report()
    {
        var west = new VanDailyTruckSummary
        {
            RouteId = 1,
            RouteCode = "WEST1",
            RouteName = "West 1",
            Territory = "Harare",
            DailySalesTarget = 3000m,
            PeriodTarget = 15000m,
            TargetToDate = 9000m,
            Sales = 9240.50m,
            Ecocash = 520.25m,
            AccountSales = 320m,
            SalesCount = 96,
            DaysReported = 2,
            WorkingDaysElapsed = 3,
            CustomersVisited = 110,
            ProductiveCalls = 88,
            NewCustomers = 4,
            PlannedCustomers = 60,
            CcrCustomersVisited = 54,
            PlanBasis = "Round",
            DaysOnWholeList = 1,
            PcrProductiveCalls = 88,
            PcrCallsMade = 112,
            Kilometres = 412,
            FuelLitres = 60m
        };

        var upcountry = new VanDailyTruckSummary
        {
            RouteId = 2,
            RouteCode = "UPC2",
            RouteName = "Upcountry 2",
            Territory = "Upcountry",
            Sales = 900m,
            SalesCount = 7,
            DaysReported = 1,
            WorkingDaysElapsed = 1,
            CustomersVisited = 9,
            ProductiveCalls = 7,
            PcrProductiveCalls = 7,
            PcrCallsMade = 9
        };

        return new VanDailyTruckReportResponse
        {
            FromDate = new DateTime(2026, 10, 1),
            ToDate = new DateTime(2026, 10, 8),
            GeneratedAtUtc = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc),
            WorkingDays = 5,
            WorkingDaysElapsed = 3,
            SalesOutsideRoutes = 412m,
            Trucks = [west, upcountry],
            Total = new VanDailyTruckSummary
            {
                RouteName = "All trucks",
                PeriodTarget = 15000m,
                TargetToDate = 9000m,
                Sales = 10140.50m,
                Ecocash = 520.25m,
                AccountSales = 320m,
                SalesCount = 103,
                DaysReported = 3,
                WorkingDaysElapsed = 4,
                CustomersVisited = 119,
                ProductiveCalls = 95,
                NewCustomers = 4,
                PlannedCustomers = 60,
                CcrCustomersVisited = 54,
                PlanBasis = "Round",
                DaysOnWholeList = 1,
                PcrProductiveCalls = 95,
                PcrCallsMade = 121,
                Kilometres = 412,
                FuelLitres = 60m
            },
            Exceptions = new VanDailyTruckExceptions { NotReported = 1, BelowTarget = 1, ReturnedLate = 1 },
            Days =
            [
                new VanDailyTruckDay
                {
                    RouteId = 1, RouteCode = "WEST1", RouteName = "West 1", Territory = "Harare",
                    TradingDate = new DateTime(2026, 10, 1), IsWorkingDay = true, IsComplete = true,
                    HasDayRecord = true, HasActivity = true,
                    AreaName = "Glen Norah", AreaSource = "Plan",
                    TimeOut = new DateTime(2026, 10, 1, 7, 42, 0), TimeIn = new DateTime(2026, 10, 1, 16, 55, 0),
                    PlannedCustomers = 60, PlannedCallsMade = 54, PlanBasis = "Round",
                    CustomersVisited = 58, ProductiveCalls = 46, CallsMade = 59, NewCustomers = 2,
                    SalesCount = 50, Sales = 4620.25m, Cash = 4100m, Ecocash = 520.25m,
                    AccountSales = 320m,
                    StartingMileage = 88100, ClosingMileage = 88228, OdometerUnit = VanOdometer.Miles, Kilometres = 206,
                    FuelLitres = 60m, DailySalesTarget = 3000m
                },
                new VanDailyTruckDay
                {
                    RouteId = 1, RouteCode = "WEST1", RouteName = "West 1", Territory = "Harare",
                    TradingDate = new DateTime(2026, 10, 2), IsWorkingDay = true, IsComplete = true,
                    HasDayRecord = true, HasActivity = true, ReturnedLate = true,
                    AreaName = "Budiriro", AreaSource = "Office",
                    TimeOut = new DateTime(2026, 10, 2, 7, 50, 0), TimeIn = new DateTime(2026, 10, 3, 6, 10, 0),
                    PlannedCustomers = 286, PlannedCallsMade = 52, PlanBasis = "List",
                    CustomersVisited = 52, ProductiveCalls = 42, CallsMade = 53, NewCustomers = 2,
                    SalesCount = 46, Sales = 4620.25m, Cash = 4620.25m,
                    StartingMileage = 88228, ClosingMileage = 88356, OdometerUnit = VanOdometer.Miles, Kilometres = 206,
                    DailySalesTarget = 3000m
                },
                new VanDailyTruckDay
                {
                    RouteId = 1, RouteCode = "WEST1", RouteName = "West 1", Territory = "Harare",
                    TradingDate = new DateTime(2026, 10, 5), IsWorkingDay = true, IsComplete = true,
                    OdometerUnit = VanOdometer.Miles, DailySalesTarget = 3000m
                },
                new VanDailyTruckDay
                {
                    RouteId = 2, RouteCode = "UPC2", RouteName = "Upcountry 2", Territory = "Upcountry",
                    TradingDate = new DateTime(2026, 10, 8), IsWorkingDay = true, IsComplete = false,
                    HasDayRecord = true, HasActivity = true,
                    TimeOut = new DateTime(2026, 10, 8, 6, 30, 0),
                    CustomersVisited = 9, ProductiveCalls = 7, CallsMade = 9,
                    SalesCount = 7, Sales = 900m, Cash = 900m,
                    StartingMileage = 120400
                }
            ]
        };
    }
}
