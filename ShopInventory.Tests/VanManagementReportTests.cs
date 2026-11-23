using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The van sales management report: the office's "Vansales Daily Report" workbook built from the month's
/// truck report. These pin the workbook's own rules — the run-rate over working days to the last report,
/// Monday weeks clipped to the month, the upper middle route, a truck-day with no trace counted as not
/// reported — and that the downloaded workbook carries the same figures as the page.
/// </summary>
public class VanManagementReportTests
{
    private readonly ReportExportService _service = new();

    // November 2026: Sunday the 1st, 21 working days. Reported through Wednesday the 18th.
    private static readonly DateTime First = new(2026, 11, 1);
    private static readonly DateTime Last = new(2026, 11, 30);
    private static readonly DateTime Through = new(2026, 11, 18);

    [Fact]
    public void The_run_rate_is_over_the_working_days_to_the_last_report()
    {
        var report = VanManagementReportBuilder.Build(Truck(), null, null);

        // 2 to 18 November holds 13 working days; 8 are left in the month.
        Assert.Equal(Through, report.ReportDate);
        Assert.Equal(13, report.WorkingDaysToReportDate);
        Assert.Equal(report.Total.Sales / 13m, report.DailyRunRate);
        Assert.Equal(report.Total.BalanceToTarget / 8m, report.RequiredPerRemainingDay);
    }

    [Fact]
    public void Weeks_run_monday_to_sunday_clipped_to_the_month_and_add_up_to_it()
    {
        var report = VanManagementReportBuilder.Build(Truck(), null, null);

        Assert.Equal((First, First), (report.Weeks[0].Start, report.Weeks[0].End));
        Assert.Equal((new DateTime(2026, 11, 2), new DateTime(2026, 11, 8)), (report.Weeks[1].Start, report.Weeks[1].End));
        Assert.Equal((new DateTime(2026, 11, 30), Last), (report.Weeks[^1].Start, report.Weeks[^1].End));
        Assert.Equal(report.Total.Sales, report.Weeks.Sum(week => week.Sales));

        // With no week named, the latest one anything was reported in.
        Assert.Equal(new DateTime(2026, 11, 16), VanManagementReportBuilder.WeekOf(report, null)!.Start);
    }

    [Fact]
    public void A_week_counts_the_days_each_truck_reported()
    {
        var report = VanManagementReportBuilder.Build(Truck(), null, null);
        var week = report.Weeks.Single(row => row.Start == new DateTime(2026, 11, 16));

        // Monday to Wednesday reported; UPC 1 was silent on the Tuesday.
        Assert.Equal(3, week.Trucks.Single(truck => truck.RouteName == "West 1").Submissions);
        Assert.Equal(2, week.Trucks.Single(truck => truck.RouteName == "UPC 1").Submissions);
        Assert.Equal(1.0, week.Trucks.Sum(truck => truck.Share ?? 0), 6);
    }

    /// <summary>The workbook took the upper of the two middle ranks when the count was even.</summary>
    [Fact]
    public void The_middle_route_is_the_upper_middle_and_spellings_fold_on_case_and_space()
    {
        var truck = Truck();
        var days = truck.Days.Where(day => day.HasActivity).Take(4).ToList();
        days[0].AreaName = "Kuwadzana";
        days[1].AreaName = "kuwadzana ";
        days[2].AreaName = "Epworth";
        days[3].AreaName = null;

        var report = VanManagementReportBuilder.Build(truck, null, null);
        var ranked = report.Routes.Ranked;

        Assert.Contains(ranked, route => route.Area == "Kuwadzana" && route.Days == 2);
        Assert.DoesNotContain(ranked, route => route.Area == "kuwadzana");
        Assert.Equal(ranked[(ranked.Count - 1) / 2], report.Routes.Middle);
        Assert.Equal(1, report.Routes.RecordsWithoutArea);
        Assert.Equal(days[3].Sales, report.Routes.SalesWithoutArea);
    }

    [Fact]
    public void The_daily_view_lists_every_truck_with_its_month_to_that_day()
    {
        var truck = Truck();
        var report = VanManagementReportBuilder.Build(truck, null, null);
        var tuesday = new DateTime(2026, 11, 17);

        var rows = VanManagementReportBuilder.DayView(report, tuesday);

        Assert.Equal(3, rows.Count);
        var upc = rows.Single(row => row.Truck.RouteName == "UPC 1");
        Assert.False(upc.Day!.HasActivity);
        Assert.Equal(
            truck.Days.Where(day => day.RouteName == "UPC 1" && day.TradingDate <= tuesday).Sum(day => day.Sales),
            upc.ToDate.Sales);

        var west = rows.Single(row => row.Truck.RouteName == "West 1");
        Assert.Equal((double)(west.ToDate.Sales / west.Truck.PeriodTarget!.Value), west.ToDate.TargetAchieved!.Value, 6);
    }

    [Fact]
    public void Insights_name_the_leader_and_say_whether_the_pace_holds()
    {
        var report = VanManagementReportBuilder.Build(Truck(), null, null);

        Assert.Equal("Target pace needs to accelerate", report.Insights[0].Title);
        Assert.Contains($"{report.Ranked[0].RouteName} leads van sales", report.Insights.Select(insight => insight.Title));
        Assert.StartsWith("Close the", report.Insights[^1].Body);
    }

    [Fact]
    public void A_month_with_no_sales_says_so_rather_than_writing_findings_over_zeros()
    {
        var report = VanManagementReportBuilder.Build(Empty(), null, null);

        Assert.Equal(["Awaiting sales"], report.Insights.Select(insight => insight.Title).ToArray());
        Assert.Null(report.DailyRunRate);
    }

    [Fact]
    public void A_breakage_by_a_rep_on_no_route_is_kept_on_its_own_row()
    {
        var report = VanManagementReportBuilder.Build(Truck(), Extras(), "Shamiso 5L");

        var noRoute = report.Breakages.Trucks.Single(truck => truck.RouteId is null);
        Assert.Equal(1, noRoute.Lines);
        Assert.Equal(report.Breakages.Units, report.Breakages.Trucks.Sum(truck => truck.Units));
        Assert.Equal(["Crushed", "No cause given"], report.Breakages.Causes.Select(cause => cause.Cause).ToArray());
        Assert.Equal(1, report.Breakages.Unvalued);
    }

    [Fact]
    public void The_product_group_adds_up_by_truck_by_item_and_by_day()
    {
        var report = VanManagementReportBuilder.Build(Truck(), Extras(), "Shamiso 5L");
        var products = report.Products;

        Assert.Equal(9m, products.Units);
        Assert.Equal(products.Units, products.Items.Sum(item => item.Units));
        Assert.Equal(products.Units, products.Days.Sum(day => day.Units));
        Assert.Equal(6m, products.Trucks.Single(truck => truck.RouteName == "West 1").Units);
    }

    // — The workbook ——————————————————————————————————————————————————————————————————————————

    [Fact]
    public void The_workbook_has_the_office_workbooks_sheets_in_its_order()
    {
        var report = VanManagementReportBuilder.Build(Truck(), Extras(), "Shamiso 5L");
        using var workbook = Open(_service.ExportVanManagementReportToExcel(report, Through, null));

        Assert.Equal(
            ["Insights Summary", "Management Overview", "Monthly Summary", "Daily Management View", "Daily Truck Cards",
             "Weekly Sales", "Route Sales Ranking", "Shamiso 5L", "Breakages", "Raw Data"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    [Fact]
    public void No_product_group_leaves_its_sheet_out()
    {
        var report = VanManagementReportBuilder.Build(Truck(), Extras(), null);
        using var workbook = Open(_service.ExportVanManagementReportToExcel(report, Through, null));

        Assert.DoesNotContain(workbook.Worksheets, sheet => sheet.Name == "Shamiso 5L");
    }

    [Fact]
    public void The_daily_sheet_carries_the_days_sales_as_numbers_and_totals_them()
    {
        var truck = Truck();
        var report = VanManagementReportBuilder.Build(truck, null, null);
        using var workbook = Open(_service.ExportVanManagementReportToExcel(report, Through, null));
        var sheet = workbook.Worksheet("Daily Management View");

        int headings = sheet.Column(1).CellsUsed().Single(cell => cell.GetString() == "Truck").Address.RowNumber;
        int sales = sheet.Row(headings).CellsUsed().Single(cell => cell.GetString() == "Daily Sales").Address.ColumnNumber;
        int west = sheet.Column(1).CellsUsed().First(cell => cell.GetString() == "West 1").Address.RowNumber;
        int total = sheet.Column(1).CellsUsed().Single(cell => cell.GetString() == "TOTAL").Address.RowNumber;

        var expected = truck.Days.Single(day => day.RouteName == "West 1" && day.TradingDate == Through).Sales;
        Assert.Equal(expected, sheet.Cell(west, sales).GetValue<decimal>());
        Assert.Equal(truck.Days.Where(day => day.TradingDate == Through).Sum(day => day.Sales), sheet.Cell(total, sales).GetValue<decimal>());
    }

    [Fact]
    public void The_monthly_summary_is_the_truck_reports_own_sheet()
    {
        var truck = Truck();
        var report = VanManagementReportBuilder.Build(truck, null, null);

        using var management = Open(_service.ExportVanManagementReportToExcel(report, Through, null));
        using var daily = Open(_service.ExportVanDailyTruckReportToExcel(truck));

        Assert.Equal(TextOf(daily.Worksheet("Trucks")), TextOf(management.Worksheet("Monthly Summary")));
        Assert.Equal(TextOf(daily.Worksheet("Days")), TextOf(management.Worksheet("Raw Data")));
    }

    [Fact]
    public void An_empty_month_still_produces_a_readable_workbook()
    {
        var report = VanManagementReportBuilder.Build(Empty(), null, null);
        using var workbook = Open(_service.ExportVanManagementReportToExcel(report, First, null));

        Assert.Contains("Awaiting sales".ToUpperInvariant(), TextOf(workbook.Worksheet("Insights Summary")));
        Assert.Contains("Nothing recorded in the period.", TextOf(workbook.Worksheet("Breakages")));
    }

    // — Data ————————————————————————————————————————————————————————————————————————————————————

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed()
            .Where(cell => cell.Address.RowNumber > 1) // the title bar carries the time it was made
            .Select(cell => cell.GetFormattedString()));

    private static readonly (int Id, string Name, decimal Daily)[] Routes =
    [
        (1, "West 1", 2500m),
        (2, "UPC 1", 3000m),
        (3, "East 1 & 2", 2500m)
    ];

    /// <summary>
    /// Three trucks reporting every working day from the 2nd to the 18th, except UPC 1 on Tuesday the
    /// 17th, which left no trace. Sales differ per truck and day so no two totals coincide.
    /// </summary>
    private static VanDailyTruckReportResponse Truck()
    {
        var days = new List<VanDailyTruckDay>();

        for (var date = First; date <= Through; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            foreach (var (id, name, daily) in Routes)
            {
                var silent = id == 2 && date == new DateTime(2026, 11, 17);
                var sales = silent ? 0m : 1000m + id * 150m + date.Day * 7.25m;

                days.Add(new VanDailyTruckDay
                {
                    RouteId = id,
                    RouteCode = $"R{id}",
                    RouteName = name,
                    TradingDate = date,
                    IsWorkingDay = true,
                    IsComplete = true,
                    HasDayRecord = !silent,
                    HasActivity = !silent,
                    AreaName = silent ? null : $"Area {(date.Day + id) % 5}",
                    PlannedCustomers = silent ? null : 40,
                    PlanBasis = silent ? null : "Round",
                    PlannedCallsMade = silent ? 0 : 30 + id,
                    CustomersVisited = silent ? 0 : 32 + id,
                    CallsMade = silent ? 0 : 32 + id,
                    ProductiveCalls = silent ? 0 : 20 + id,
                    Sales = sales,
                    Cash = sales - (silent ? 0m : 25m),
                    Ecocash = silent ? 0m : 25m,
                    SalesCount = silent ? 0 : 20 + id,
                    Kilometres = silent ? null : 60 + id * 20,
                    FuelLitres = date.DayOfWeek == DayOfWeek.Monday ? 100m : null,
                    DailySalesTarget = daily,
                    AccountSales = id == 3 && date.Day == 10 ? 450m : null,
                    Notes = id == 1 && date.Day == 12 ? "Truck breakdown" : null
                });
            }
        }

        var trucks = Routes.Select(route => Summary(route.Id, route.Name, route.Daily, days.Where(day => day.RouteId == route.Id).ToList())).ToList();

        return new VanDailyTruckReportResponse
        {
            FromDate = First,
            ToDate = Last,
            GeneratedAtUtc = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc),
            WorkingDays = 21,
            WorkingDaysElapsed = 13,
            Trucks = trucks,
            Days = days,
            Total = Summary(null, "All trucks", Routes.Sum(route => route.Daily), days)
        };
    }

    private static VanDailyTruckSummary Summary(int? id, string name, decimal daily, IReadOnlyCollection<VanDailyTruckDay> days) => new()
    {
        RouteId = id,
        RouteCode = id is null ? "ALL" : $"R{id}",
        RouteName = name,
        DailySalesTarget = daily,
        PeriodTarget = daily * 21,
        TargetToDate = daily * 13,
        Sales = days.Sum(day => day.Sales),
        Ecocash = days.Sum(day => day.Ecocash),
        AccountSales = days.Sum(day => day.AccountSales ?? 0m),
        SalesCount = days.Sum(day => day.SalesCount),
        DaysReported = days.Count(day => day.HasActivity),
        WorkingDaysElapsed = id is null ? days.Count : 13,
        CustomersVisited = days.Sum(day => day.CustomersVisited),
        ProductiveCalls = days.Sum(day => day.ProductiveCalls),
        PlannedCustomers = days.Sum(day => day.PlannedCustomers ?? 0),
        CcrCustomersVisited = days.Sum(day => day.PlannedCallsMade),
        PlanBasis = "Round",
        PcrProductiveCalls = days.Sum(day => day.ProductiveCalls),
        PcrCallsMade = days.Sum(day => day.CallsMade),
        Kilometres = days.Sum(day => day.Kilometres ?? 0),
        FuelLitres = days.Sum(day => day.FuelLitres ?? 0m)
    };

    private static VanDailyTruckReportResponse Empty() => new()
    {
        FromDate = First,
        ToDate = Last,
        WorkingDays = 21,
        Trucks = Routes.Select(route => new VanDailyTruckSummary { RouteId = route.Id, RouteName = route.Name, PeriodTarget = route.Daily * 21 }).ToList(),
        Total = new VanDailyTruckSummary { RouteName = "All trucks", PeriodTarget = 168000m }
    };

    private static VanManagementExtrasResponse Extras() => new()
    {
        FromDate = First,
        ToDate = Last,
        Products =
        [
            new() { RouteId = 1, TradingDate = new DateTime(2026, 11, 3), ItemCode = "ICS003", ItemName = "5 Litre Shamiso Icecream", Units = 4m, Value = 29.04m },
            new() { RouteId = 1, TradingDate = new DateTime(2026, 11, 4), ItemCode = "ICS024", ItemName = "5 Litre Shamiso Vanilla", Units = 2m, Value = 14.44m },
            new() { RouteId = 2, TradingDate = new DateTime(2026, 11, 4), ItemCode = "ICS003", ItemName = "5 Litre Shamiso Icecream", Units = 3m, Value = 21.78m }
        ],
        Breakages =
        [
            new() { BreakageId = 1, RouteId = 1, TradingDate = new DateTime(2026, 11, 13), ReportedBy = "West One", ItemCode = "YOG143", Reason = "Crushed", Units = 6m, IsConfirmed = true, Status = "Transferred", ValueBeforeVat = 6.24m },
            new() { BreakageId = 2, RouteId = 3, TradingDate = new DateTime(2026, 11, 13), ReportedBy = "East One", ItemCode = "YOG150", Reason = null, Units = 2m, Status = "Pending", ValueBeforeVat = null },
            new() { BreakageId = 3, RouteId = null, TradingDate = new DateTime(2026, 11, 13), ReportedBy = "Relief Rep", ItemCode = "YOG020", Reason = "crushed", Units = 1m, Status = "Pending", ValueBeforeVat = 0.86m }
        ]
    };
}
