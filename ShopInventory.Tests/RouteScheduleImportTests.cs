using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers;
using ShopInventory.Features.RouteCustomers.Commands.ImportRouteSchedule;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Web.Features.RouteCustomers;
using WebErrors = ShopInventory.Web.Common.Errors.Errors;

namespace ShopInventory.Tests;

/// <summary>
/// The office's "Customer Database per Route" workbook, read into the weekdays each van calls on each
/// shop — the plan the CCR is measured against.
/// </summary>
/// <remarks>
/// The workbook below is made up, but laid out the way the real one is (October 2026): a weekday
/// heading per column, the calling order in the next column, area headings with no number, a Total
/// service station that is a shop, a summary block after a gap, a column with no weekday, and a
/// weekday used twice.
/// </remarks>
public sealed class RouteScheduleImportTests : IDisposable
{
    private const string West1 = "VAN009";
    private const string East = "VAN013";
    private static readonly Guid Office = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public RouteScheduleImportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        var west1 = new RouteEntity { Code = "WEST1", Name = "West 1 Truck", Territory = "Harare" };
        var east = new RouteEntity { Code = "EAST", Name = "East Truck", Territory = "Harare" };
        _context.Routes.AddRange(west1, east);
        _context.SaveChanges();

        _context.Users.AddRange(
            Rep("west1a", West1, west1.Id),
            Rep("west1b", West1, west1.Id),
            Rep("east", East, east.Id));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Reading the workbook ───────────────────────────────────────────────

    [Fact]
    public void Each_weekday_column_is_read_with_its_calling_order()
    {
        var read = RouteScheduleWorkbook.Read(Workbook()).Value;

        var sheet = Assert.Single(read.Sheets);
        Assert.Equal("WEST 1", sheet.Name);

        var monday = sheet.Outlets.Where(o => o.Day == DayOfWeek.Monday).ToList();
        Assert.Equal(["Budiriro 2", "Hillside Groceries", "Corner Groceries", "Total", "Bluebird"], monday.Select(o => o.Name));
        Assert.Equal([null, 1, 2, 3, 4], monday.Select(o => o.Sequence));
        Assert.Equal("WEST 1!B3", monday[1].Ref);

        // The sheet with no SEQ heading: the number beside the name is still its order.
        var tuesday = sheet.Outlets.Where(o => o.Day == DayOfWeek.Tuesday).ToList();
        Assert.Equal(["Sunrise Investments", "Lark Mini"], tuesday.Select(o => o.Name));
        Assert.Equal([1, 2], tuesday.Select(o => o.Sequence));
    }

    /// <summary>
    /// "Total" is a service station on the CZA Tuesday round, numbered like every other shop. Reading it
    /// as the end of the list lost the 22 shops after it.
    /// </summary>
    [Fact]
    public void A_shop_called_Total_does_not_end_the_list()
    {
        var read = RouteScheduleWorkbook.Read(Workbook()).Value;

        Assert.Contains(read.Sheets[0].Outlets, o => o.Name == "Bluebird" && o.Day == DayOfWeek.Monday);
    }

    [Fact]
    public void Nothing_left_unread_is_dropped_without_saying_so()
    {
        var sheet = RouteScheduleWorkbook.Read(Workbook()).Value.Sheets[0];

        Assert.DoesNotContain(sheet.Outlets, o => o.Name is "New Customers" or "Late Shop" or "Hilltop Shop" or "Hope Shop");
        Assert.Equal(3, sheet.Skipped.Count);
        Assert.Contains(sheet.Skipped, note => note.Contains("2 names further down the Monday column") && note.Contains("New Customers"));
        Assert.Contains(sheet.Skipped, note => note.Contains("Column K") && note.Contains("no weekday heading") && note.Contains("Hilltop"));
        Assert.Contains(sheet.Skipped, note => note.Contains("second Tuesday") && note.Contains("Hope Shop"));
    }

    [Fact]
    public void A_file_that_is_not_a_workbook_is_refused()
    {
        var read = RouteScheduleWorkbook.Read(new MemoryStream("not a workbook"u8.ToArray()));

        Assert.Equal(WebErrors.RouteSchedule.FileUnreadable, read.FirstError);
    }

    [Fact]
    public void A_workbook_with_no_weekday_headings_is_refused()
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("Sheet1").Cell(1, 1).Value = "Customer";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        Assert.Equal(WebErrors.RouteSchedule.NoWeekdays, RouteScheduleWorkbook.Read(stream).FirstError);
    }

    // ── Matching names ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("KUDU 2", "Exact", 1)]
    [InlineData("M & M", "Exact", 2)]
    [InlineData("Zebra", "Close", 3)]
    [InlineData("Blessed Shop", "Close", 4)]
    public void A_name_matches_a_shop_only_when_it_is_certain(string name, string status, int shopId)
    {
        var match = Matcher().Find(name);

        Assert.Equal(status, match.Status);
        Assert.Equal(shopId, match.Shop?.Id);
    }

    [Fact]
    public void A_name_two_shops_carry_is_ambiguous_and_offers_both()
    {
        var match = Matcher().Find("lion");

        Assert.Equal(RouteScheduleMatchStatus.Ambiguous, match.Status);
        Assert.Null(match.Shop);
        Assert.Equal([5, 6], match.Candidates.Select(c => c.Id));
    }

    [Fact]
    public void A_misspelling_is_offered_never_assumed()
    {
        var match = Matcher().Find("Riverside Hotal");

        Assert.Equal(RouteScheduleMatchStatus.Unmatched, match.Status);
        Assert.Null(match.Shop);
        Assert.Equal(7, match.Candidates[0].Id);
    }

    [Fact]
    public void An_area_heading_matches_nothing()
    {
        var match = Matcher().Find("Kambuzuma Section 5");

        Assert.Equal(RouteScheduleMatchStatus.Unmatched, match.Status);
        Assert.Empty(match.Candidates);
    }

    // ── Checking and saving ────────────────────────────────────────────────

    [Fact]
    public async Task The_truck_is_worked_out_from_the_sheet_name()
    {
        await AddShopsAsync(West1, ("W1", "Bluebird"));

        var result = await ImportAsync(validateOnly: true, Sheet("WEST 1", null, ("Bluebird", DayOfWeek.Monday)));

        var sheet = Assert.Single(result.Sheets);
        Assert.Equal(West1, sheet.AccountCode);
        Assert.True(sheet.AccountGuessed);
        Assert.Equal(RouteScheduleMatchStatus.Exact, sheet.Outlets[0].Status);
    }

    [Fact]
    public async Task Checking_saves_nothing()
    {
        await AddShopsAsync(West1, ("W1", "Bluebird"));

        var result = await ImportAsync(validateOnly: true, Sheet("WEST 1", West1, ("Bluebird", DayOfWeek.Monday)));

        Assert.False(result.Applied);
        Assert.Equal(1, result.Accounts[0].ScheduledAfter);
        Assert.Empty(await _context.RouteCustomerVisitDays.ToListAsync());
    }

    [Fact]
    public async Task Saving_makes_the_sheet_the_trucks_whole_schedule()
    {
        var ids = await AddShopsAsync(West1, ("W1", "Bluebird"), ("W2", "Lark Mini"), ("W3", "Dropped Shop"));
        var eastShop = (await AddShopsAsync(East, ("E1", "Bluebird")))[0];

        // Before: a day for a shop the sheet no longer names, and a day on another truck.
        _context.RouteCustomerVisitDays.AddRange(
            new RouteCustomerVisitDayEntity { RouteCustomerId = ids[2], DayOfWeek = DayOfWeek.Friday },
            new RouteCustomerVisitDayEntity { RouteCustomerId = eastShop, DayOfWeek = DayOfWeek.Friday });
        await _context.SaveChangesAsync();

        var result = await ImportAsync(
            validateOnly: false,
            Sheet("WEST 1", West1, ("Bluebird", DayOfWeek.Monday), ("Lark Mini", DayOfWeek.Tuesday), ("Bluebird", DayOfWeek.Thursday)));

        Assert.True(result.Applied);
        var summary = Assert.Single(result.Accounts);
        Assert.True(summary.Applied);
        Assert.Equal(1, summary.ScheduledBefore);
        Assert.Equal(2, summary.ScheduledAfter);

        Assert.Equal(
            [(ids[0], DayOfWeek.Monday), (ids[0], DayOfWeek.Thursday), (ids[1], DayOfWeek.Tuesday), (eastShop, DayOfWeek.Friday)],
            await VisitDaysAsync());
    }

    [Fact]
    public async Task Two_sheets_for_one_truck_make_one_schedule()
    {
        var ids = await AddShopsAsync(West1, ("W1", "Bluebird"), ("W2", "Lark Mini"));

        await ImportAsync(
            validateOnly: false,
            Sheet("CBD", West1, ("Bluebird", DayOfWeek.Wednesday)),
            Sheet("CZA", West1, ("Lark Mini", DayOfWeek.Monday)));

        Assert.Equal([(ids[0], DayOfWeek.Wednesday), (ids[1], DayOfWeek.Monday)], await VisitDaysAsync());
    }

    [Fact]
    public async Task A_truck_on_which_nothing_matched_is_left_alone()
    {
        var ids = await AddShopsAsync(West1, ("W1", "Bluebird"));
        _context.RouteCustomerVisitDays.Add(new RouteCustomerVisitDayEntity { RouteCustomerId = ids[0], DayOfWeek = DayOfWeek.Friday });
        await _context.SaveChangesAsync();

        var result = await ImportAsync(validateOnly: false, Sheet("WEST 1", West1, ("Somebody Else", DayOfWeek.Monday)));

        Assert.False(result.Applied);
        Assert.False(result.Accounts[0].Applied);
        Assert.NotNull(result.Accounts[0].Note);
        Assert.Equal([(ids[0], DayOfWeek.Friday)], await VisitDaysAsync());
    }

    [Fact]
    public async Task The_office_can_pick_a_shop_or_leave_one_out_but_only_on_that_truck()
    {
        var ids = await AddShopsAsync(West1, ("W1", "Riverside Hotel"), ("W2", "Lion"), ("W3", "Lion"));
        var eastShop = (await AddShopsAsync(East, ("E1", "Elsewhere")))[0];

        var sheet = Sheet("WEST 1", West1,
            ("Riverside Hotal", DayOfWeek.Monday),
            ("Lion", DayOfWeek.Monday),
            ("Lion", DayOfWeek.Tuesday));
        sheet.Outlets[0].PickedRouteCustomerId = ids[0];
        sheet.Outlets[1].PickedRouteCustomerId = eastShop;
        sheet.Outlets[2].Excluded = true;

        var result = await ImportAsync(validateOnly: false, sheet);

        Assert.Equal(
            [RouteScheduleMatchStatus.Picked, RouteScheduleMatchStatus.PickRejected, RouteScheduleMatchStatus.Excluded],
            result.Sheets[0].Outlets.Select(o => o.Status));
        Assert.Equal([(ids[0], DayOfWeek.Monday)], await VisitDaysAsync());
    }

    [Fact]
    public async Task A_removed_shop_is_not_matched()
    {
        await AddShopsAsync(West1, ("W1", "Bluebird"));
        await _context.RouteCustomers.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, false));

        var result = await ImportAsync(validateOnly: true, Sheet("WEST 1", West1, ("Bluebird", DayOfWeek.Monday)));

        Assert.Equal(RouteScheduleMatchStatus.Unmatched, result.Sheets[0].Outlets[0].Status);
    }

    /// <summary>
    /// The point of the import: once saved, a day's round — the CCR's denominator — is the shops the
    /// workbook gives that weekday, not the truck's whole customer list.
    /// </summary>
    [Fact]
    public async Task A_saved_schedule_is_the_round_the_CCR_is_measured_against()
    {
        await AddShopsAsync(West1, ("W1", "Bluebird"), ("W2", "Lark Mini"), ("W3", "Not On Monday"));

        await ImportAsync(
            validateOnly: false,
            Sheet("WEST 1", West1, ("Bluebird", DayOfWeek.Monday), ("Lark Mini", DayOfWeek.Monday), ("Not On Monday", DayOfWeek.Friday)));

        var monday = new DateTime(2026, 10, 19);
        var plans = await VanDayPlans.LoadAsync(_context, monday, monday, CancellationToken.None);
        var plan = plans.PlanFor(VanTruckRoutes.TruckOfAccount(West1), monday);

        Assert.NotNull(plan);
        Assert.Equal(["W1", "W2"], plan.Shops.Order());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static RouteScheduleMatcher Matcher() => new(
    [
        new(1, "C1", "Kudu2"),
        new(2, "C2", "M&M"),
        new(3, "C3", "Zebra Investments"),
        new(4, "C4", "Blessed Groceries"),
        new(5, "C5", "Lion"),
        new(6, "C6", "LION"),
        new(7, "C7", "Riverside Hotel"),
    ]);

    private async Task<RouteScheduleImportResultDto> ImportAsync(bool validateOnly, params RouteScheduleImportSheet[] sheets)
    {
        var handler = new ImportRouteScheduleHandler(_context, NullLogger<ImportRouteScheduleHandler>.Instance);
        var result = await handler.Handle(
            new ImportRouteScheduleCommand(new RouteScheduleImportRequest { ValidateOnly = validateOnly, Sheets = [.. sheets] }, Office),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private static RouteScheduleImportSheet Sheet(string name, string? account, params (string Name, DayOfWeek Day)[] outlets) => new()
    {
        Name = name,
        AccountCode = account,
        Outlets = outlets
            .Select((outlet, index) => new RouteScheduleImportOutlet
            {
                Ref = $"{name}!B{index + 3}",
                Name = outlet.Name,
                Day = outlet.Day,
                Sequence = index + 1,
            })
            .ToList(),
    };

    private async Task<List<int>> AddShopsAsync(string account, params (string Code, string Name)[] shops)
    {
        var rows = shops.Select(shop => new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = account,
            Code = shop.Code,
            Name = shop.Name,
            IsActive = true,
        }).ToList();

        _context.RouteCustomers.AddRange(rows);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return rows.Select(row => row.Id).ToList();
    }

    private async Task<List<(int, DayOfWeek)>> VisitDaysAsync() =>
        (await _context.RouteCustomerVisitDays.AsNoTracking().ToListAsync())
            .Select(day => (day.RouteCustomerId, day.DayOfWeek))
            .OrderBy(day => day.RouteCustomerId)
            .ThenBy(day => day.DayOfWeek)
            .ToList();

    private static User Rep(string username, string account, int routeId) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = $"{username}@example.com",
        PasswordHash = "x",
        Role = "Sales",
        IsActive = true,
        AssignedBusinessPartnerCode = account,
        RouteId = routeId,
    };

    /// <summary>A sheet laid out like the office's: see the class remarks.</summary>
    private static MemoryStream Workbook()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("WEST 1");

        sheet.Cell("A1").Value = "OUTLET ID ";
        sheet.Cell("B1").Value = "MONDAY";
        sheet.Cell("C1").Value = "SEQ";
        sheet.Cell("B2").Value = "Budiriro 2";
        sheet.Cell("B3").Value = "Hillside Groceries";
        sheet.Cell("C3").Value = 1;
        sheet.Cell("B4").Value = "Corner Groceries";
        sheet.Cell("C4").Value = 2;
        sheet.Cell("B5").Value = "Total";
        sheet.Cell("C5").Value = 3;
        sheet.Cell("B6").Value = "Bluebird";
        sheet.Cell("C6").Value = 4;

        // After a long gap: the summary block, and a list somebody parked below it.
        sheet.Cell("B20").Value = "New Customers";
        sheet.Cell("B21").Value = "Late Shop";
        sheet.Cell("B30").Value = "Total";
        sheet.Cell("C30").Value = 4;
        sheet.Cell("B31").Value = "Comments ";

        // No SEQ heading, but numbers beside the names all the same.
        sheet.Cell("G1").Value = "Tuesday ";
        sheet.Cell("G2").Value = "Sunrise Investments";
        sheet.Cell("H2").Value = 1;
        sheet.Cell("G3").Value = "Lark Mini ";
        sheet.Cell("H3").Value = 2;

        // A list with a SEQ column and no weekday above it.
        sheet.Cell("J1").Value = "OUTLET ID ";
        sheet.Cell("L1").Value = "SEQ";
        sheet.Cell("K2").Value = "Hilltop Shop";
        sheet.Cell("L2").Value = 1;

        // A second Tuesday.
        sheet.Cell("N1").Value = "TUESDAY";
        sheet.Cell("O1").Value = "SEQ";
        sheet.Cell("N2").Value = "Hope Shop";
        sheet.Cell("O2").Value = 1;

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
