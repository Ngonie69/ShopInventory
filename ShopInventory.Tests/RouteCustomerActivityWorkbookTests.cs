using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ShopInventory.Web.Features.RouteCustomers;
using ShopInventory.Web.Features.RouteCustomers.Queries.ExportRouteCustomerActivity;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Route Customers page's trading-activity classing and its workbook: the classing checked at the edges
/// of the window, then the file opened, validated and read back.
/// </summary>
/// <remarks>
/// The fixture, against a 90-day window ending 20 Nov 2026:
///
///   VAN001  Alpha    last sale 2 days ago, 5 sales, USD 500        active
///           Bravo    last sale 89 days ago, 1 sale, USD 40         active — the window's first day
///           Charlie  last sale 90 days ago                         dormant — the day before the window
///           Delta    never bought, captured 30 days ago            never bought
///           Echo     removed from the vans, last sale 200 days ago dormant
///   VAN002  Foxtrot  last sale 10 days ago, 3 sales, ZWG 9,000     active
///           Golf     record deleted, a sale 5 days ago, USD 20     active
/// </remarks>
public sealed class RouteCustomerActivityWorkbookTests
{
    private static readonly DateTime Today = new(2026, 11, 20);
    private const int Window = 90;

    private readonly ReportExportService _export = new();

    // ── Classing ───────────────────────────────────────────────────────────

    [Fact]
    public void A_sale_on_the_first_day_of_the_window_is_active_and_the_day_before_is_dormant()
    {
        var customers = Customers();

        Assert.Equal(RouteCustomerActivityState.Active, RouteCustomerActivity.Classify(Named(customers, "Bravo"), Window));
        Assert.Equal(RouteCustomerActivityState.Dormant, RouteCustomerActivity.Classify(Named(customers, "Charlie"), Window));
        Assert.Equal(RouteCustomerActivityState.NeverBought, RouteCustomerActivity.Classify(Named(customers, "Delta"), Window));

        // The same shop reads differently against a shorter window, and never-bought does not move.
        Assert.Equal(RouteCustomerActivityState.Dormant, RouteCustomerActivity.Classify(Named(customers, "Bravo"), 30));
        Assert.Equal(RouteCustomerActivityState.NeverBought, RouteCustomerActivity.Classify(Named(customers, "Delta"), 180));
    }

    [Fact]
    public void Inactive_is_dormant_and_never_bought_together()
    {
        var states = Customers().Select(customer => RouteCustomerActivity.Classify(customer, Window)).ToList();

        // Alpha, Bravo, Foxtrot, Golf; Charlie, Echo, Hotel; Delta.
        Assert.Equal(4, states.Count(state => RouteCustomerActivity.Matches(state, RouteCustomerActivityFilter.Active)));
        Assert.Equal(3, states.Count(state => RouteCustomerActivity.Matches(state, RouteCustomerActivityFilter.Dormant)));
        Assert.Equal(1, states.Count(state => RouteCustomerActivity.Matches(state, RouteCustomerActivityFilter.NeverBought)));
        Assert.Equal(4, states.Count(state => RouteCustomerActivity.Matches(state, RouteCustomerActivityFilter.Inactive)));
        Assert.Equal(8, states.Count(state => RouteCustomerActivity.Matches(state, RouteCustomerActivityFilter.All)));
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(180, 180)]
    [InlineData(45, RouteCustomerActivity.DefaultWindowDays)]
    [InlineData(0, RouteCustomerActivity.DefaultWindowDays)]
    public void Only_the_offered_windows_are_used(int asked, int expected)
    {
        Assert.Equal(expected, RouteCustomerActivity.NormalizeWindow(asked));
    }

    [Fact]
    public void The_window_and_the_APIs_dormancy_threshold_agree()
    {
        // A 90-day window ending on the 20th starts on 23 Aug; the API calls a shop dormant after more than
        // 89 days, so the two put the same shop on the same side.
        Assert.Equal(new DateTime(2026, 8, 23), RouteCustomerActivity.WindowStart(Today, Window));
        Assert.Equal(89, RouteCustomerActivity.DormantDaysFor(Window));
    }

    // ── Which rows ─────────────────────────────────────────────────────────

    [Fact]
    public void The_current_view_exports_the_pages_customers_and_nothing_else()
    {
        var rows = ExportRouteCustomerActivityHandler.SelectRows(Summary(), new HashSet<int> { 1, 4 }, includeRemoved: true);

        Assert.Equal(["Alpha", "Delta"], rows.Select(row => row.Name).Order());
    }

    [Fact]
    public void Every_customer_leaves_out_the_removed_and_deleted_unless_asked()
    {
        var onTheVans = ExportRouteCustomerActivityHandler.SelectRows(Summary(), null, includeRemoved: false);
        var everyone = ExportRouteCustomerActivityHandler.SelectRows(Summary(), null, includeRemoved: true);

        Assert.DoesNotContain(onTheVans, row => row.Name is "Echo" or "Golf");
        Assert.Equal(5, onTheVans.Count);
        Assert.Equal(7, everyone.Count);
    }

    // ── The workbook ───────────────────────────────────────────────────────

    [Fact]
    public void The_workbook_is_valid_with_five_sheets()
    {
        var bytes = _export.ExportRouteCustomerActivityToExcel(Report());

        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(error => $"{error.Path?.XPath}: {error.Description}")));

        var names = document.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(sheet => sheet.Name!.Value);
        Assert.Equal(["Summary", "Customers", "Follow-up", "Never bought", "By month"], names);
    }

    [Fact]
    public void Only_the_chosen_sheets_are_written_and_by_month_needs_a_history()
    {
        var chosen = Report() with { Sheets = RouteCustomerActivitySheets.Customers | RouteCustomerActivitySheets.FollowUp };
        using (var workbook = Open(_export.ExportRouteCustomerActivityToExcel(chosen)))
        {
            Assert.Equal(["Customers", "Follow-up"], workbook.Worksheets.Select(sheet => sheet.Name));
        }

        var noHistory = Report();
        noHistory.Summary.HistoryMonths.Clear();
        using (var workbook = Open(_export.ExportRouteCustomerActivityToExcel(noHistory)))
        {
            Assert.DoesNotContain(workbook.Worksheets, sheet => sheet.Name == "By month");
        }
    }

    [Fact]
    public void The_summary_counts_each_route_and_states_the_overall_rate()
    {
        using var workbook = Open(_export.ExportRouteCustomerActivityToExcel(Report()));
        var ws = workbook.Worksheet("Summary");

        var header = HeaderRow(ws);
        Assert.Equal(["Route", "Customers", "Active", "Dormant", "Never bought", "Active rate", "Sales (90 d)", "Value (USD)", "Value (ZWG)"],
            Headers(ws, header));

        var van001 = ws.Row(header + 1);
        Assert.Equal("VAN001 - Van Sales West", van001.Cell(1).GetString());
        Assert.Equal([5, 2, 2, 1], Enumerable.Range(2, 4).Select(column => van001.Cell(column).GetValue<int>()));
        Assert.Equal(0.4, van001.Cell(6).GetDouble(), 3);
        Assert.Equal(540, van001.Cell(8).GetDouble(), 2);

        var van002 = ws.Row(header + 2);
        Assert.Equal([2, 2, 0, 0], Enumerable.Range(2, 4).Select(column => van002.Cell(column).GetValue<int>()));

        // Totals are formulas, so they follow the filter.
        Assert.Equal("Total", ws.Row(header + 3).Cell(1).GetString());
        Assert.StartsWith("SUBTOTAL(109", ws.Row(header + 3).Cell(2).FormulaA1);

        // The cards: seven customers, four active.
        var cardRow = Enumerable.Range(1, header).First(row => ws.Cell(row + 1, 1).GetString() == "Customers");
        Assert.Equal(7, ws.Cell(cardRow, 1).GetValue<int>());
        Assert.Equal(4, ws.Cell(cardRow, 2).GetValue<int>());
        Assert.Equal(4.0 / 7, ws.Cell(cardRow, 5).GetDouble(), 3);
    }

    [Fact]
    public void The_customers_sheet_classes_every_shop_and_carries_its_record()
    {
        using var workbook = Open(_export.ExportRouteCustomerActivityToExcel(Report()));
        var ws = workbook.Worksheet("Customers");
        var header = HeaderRow(ws);
        var columns = Headers(ws, header);

        Assert.Equal(["Route", "Code", "Customer", "Phone", "Email", "Address", "VAT no.", "On vans", "Captured", "Captured by",
            "First sale", "Last sale", "Days since", "Activity", "Sales (90 d)", "Lines (90 d)", "Open orders", "Value (USD)", "Value (ZWG)"], columns);

        string Cell(string customer, string column) =>
            ws.Row(RowOf(ws, header, customer)).Cell(columns.IndexOf(column) + 1).GetString();

        Assert.Equal("Active", Cell("Bravo", "Activity"));
        Assert.Equal("Dormant", Cell("Charlie", "Activity"));
        Assert.Equal("Never bought", Cell("Delta", "Activity"));
        Assert.Equal("Removed", Cell("Echo", "On vans"));
        Assert.Equal("Deleted", Cell("Golf", "On vans"));
        Assert.Equal("alpha@example.test", Cell("Alpha", "Email"));
        Assert.Equal("Agent One", Cell("Alpha", "Captured by"));
        Assert.Equal("", Cell("Golf", "Captured by"));

        var activity = ws.Row(RowOf(ws, header, "Charlie")).Cell(columns.IndexOf("Activity") + 1);
        Assert.True(activity.Style.Font.Bold);
    }

    [Fact]
    public void The_follow_up_lists_the_dormant_most_recently_lapsed_first_with_room_for_the_call()
    {
        using var workbook = Open(_export.ExportRouteCustomerActivityToExcel(Report()));
        var ws = workbook.Worksheet("Follow-up");
        var header = HeaderRow(ws);
        var columns = Headers(ws, header);

        // Dormant only, and within the route the shortest silence first.
        Assert.Equal(["Charlie", "Echo"], [ws.Cell(header + 1, 3).GetString(), ws.Cell(header + 2, 3).GetString()]);
        Assert.Equal("", ws.Cell(header + 3, 3).GetString());
        Assert.Equal(["Called on", "Outcome"], columns.TakeLast(2));

        // Painted after the table, so the stripes do not cover it.
        var outcome = ws.Cell(header + 2, columns.Count);
        Assert.Equal(XLColor.FromHtml("#FFFDE7"), outcome.Style.Fill.BackgroundColor);
    }

    [Fact]
    public void The_never_bought_sheet_counts_days_since_capture()
    {
        using var workbook = Open(_export.ExportRouteCustomerActivityToExcel(Report()));
        var ws = workbook.Worksheet("Never bought");
        var header = HeaderRow(ws);
        var columns = Headers(ws, header);

        Assert.Equal("Delta", ws.Cell(header + 1, 3).GetString());
        Assert.Equal(30, ws.Cell(header + 1, columns.IndexOf("Days since captured") + 1).GetValue<int>());
        Assert.Equal("Total", ws.Cell(header + 2, 1).GetString());
    }

    [Fact]
    public void By_month_names_the_months_and_shades_the_busy_ones()
    {
        using var workbook = Open(_export.ExportRouteCustomerActivityToExcel(Report()));
        var ws = workbook.Worksheet("By month");
        var header = HeaderRow(ws);
        var columns = Headers(ws, header);

        Assert.Equal(["Route", "Code", "Customer", "Activity", "Jun 2026", "Jul 2026", "Aug 2026", "Sep 2026", "Oct 2026",
            "Nov 2026 (to 20)", "6-month total"], columns);

        var alpha = ws.Row(RowOf(ws, header, "Alpha"));
        Assert.Equal([0, 1, 0, 2, 0, 5], Enumerable.Range(5, 6).Select(column => alpha.Cell(column).GetValue<int>()));
        Assert.Equal(8, alpha.Cell(11).GetValue<int>());
        Assert.Equal(XLColor.FromHtml("#3949AB"), alpha.Cell(10).Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.FromHtml("#E8EAF6"), alpha.Cell(6).Style.Fill.BackgroundColor);
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    private static RouteCustomerActivityReport Report()
    {
        var summary = Summary();
        return new RouteCustomerActivityReport(
            summary,
            Window,
            ExportRouteCustomerActivityHandler.SelectRows(summary, null, includeRemoved: true),
            Details(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["VAN001"] = "VAN001 - Van Sales West",
                ["VAN002"] = "VAN002 - Van Sales East"
            },
            "Every route customer, removed and deleted included",
            RouteCustomerActivitySheets.All,
            Today);
    }

    private static List<RouteCustomerSalesRowModel> Customers() =>
        Summary().Routes.SelectMany(route => route.Customers).Append(Customer(8, "H1", "Hotel", "VAN002", 0, 300)).ToList();

    private static RouteCustomerSalesSummaryModel Summary() => new()
    {
        From = RouteCustomerActivity.WindowStart(Today, Window),
        To = Today,
        DormantDays = RouteCustomerActivity.DormantDaysFor(Window),
        HistoryMonths = Enumerable.Range(0, 6).Select(offset => new DateTime(2026, 6, 1).AddMonths(offset)).ToList(),
        Routes =
        [
            new RouteSalesGroupModel
            {
                AssignedBusinessPartnerCode = "VAN001",
                Customers =
                [
                    Customer(1, "A1", "Alpha", "VAN001", 5, 2, [0, 1, 0, 2, 0, 5], Usd(5, 500m)),
                    Customer(2, "B1", "Bravo", "VAN001", 1, 89, [0, 0, 1, 0, 0, 0], Usd(1, 40m)),
                    Customer(3, "C1", "Charlie", "VAN001", 0, 90),
                    Customer(4, "D1", "Delta", "VAN001", 0, null),
                    Customer(5, "E1", "Echo", "VAN001", 0, 200, active: false)
                ]
            },
            new RouteSalesGroupModel
            {
                AssignedBusinessPartnerCode = "VAN002",
                Customers =
                [
                    Customer(6, "F1", "Foxtrot", "VAN002", 3, 10, [0, 0, 0, 0, 1, 2],
                        new RouteCustomerSalesTotalsModel { Currency = "ZWG", SaleCount = 3, Gross = 9_000m }),
                    Customer(null, "G1", "Golf", "VAN002", 1, 5, [0, 0, 0, 0, 0, 1], Usd(1, 20m))
                ]
            }
        ]
    };

    private static RouteCustomerSalesTotalsModel Usd(int sales, decimal gross) => new() { Currency = "USD", SaleCount = sales, Gross = gross };

    private static RouteCustomerSalesRowModel Customer(
        int? id, string code, string name, string route, int sales, int? daysSince,
        int[]? months = null, RouteCustomerSalesTotalsModel? totals = null, bool active = true) => new()
    {
        RouteCustomerId = id,
        Code = code,
        Name = name,
        AssignedBusinessPartnerCode = route,
        IsActive = id is not null && active,
        IsDeleted = id is null,
        Phone = "077 000 000",
        CapturedAt = name == "Delta" ? Today.AddDays(-30) : Today.AddDays(-400),
        SaleCount = sales,
        LineCount = sales * 2,
        FirstSaleAt = daysSince is null ? null : Today.AddDays(-350),
        LastSaleAt = daysSince is { } days ? Today.AddDays(-days) : null,
        DaysSinceLastSale = daysSince,
        TotalsByCurrency = totals is null ? [] : [totals],
        MonthlySaleCounts = [.. months ?? [0, 0, 0, 0, 0, 0]]
    };

    private static Dictionary<int, RouteCustomerModel> Details() =>
        Enumerable.Range(1, 6).ToDictionary(id => id, id => new RouteCustomerModel
        {
            Id = id,
            Email = id == 1 ? "alpha@example.test" : null,
            Address = $"Stand {id}",
            CreatedByUserName = id == 1 ? "Agent One" : null
        });

    private static RouteCustomerSalesRowModel Named(IEnumerable<RouteCustomerSalesRowModel> customers, string name) =>
        customers.Single(customer => customer.Name == name);

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    /// <summary>The table's heading row: the first row below the letterhead whose first cell says "Route".</summary>
    private static int HeaderRow(IXLWorksheet ws) =>
        Enumerable.Range(7, 40).First(row => ws.Cell(row, 1).GetString() == "Route");

    private static List<string> Headers(IXLWorksheet ws, int header) =>
        ws.Row(header).CellsUsed().Select(cell => cell.GetString()).ToList();

    private static int RowOf(IXLWorksheet ws, int header, string customer) =>
        Enumerable.Range(header + 1, 40).First(row => ws.Cell(row, 3).GetString() == customer);
}
