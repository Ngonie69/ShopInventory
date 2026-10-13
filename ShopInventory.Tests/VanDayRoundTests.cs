using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;
using ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// What a truck's calls are measured against: the day's round, not its whole customer list.
/// </summary>
/// <remarks>
/// West 1 held 286 customers in October 2026 and calls on about sixty a day, a different sixty on each
/// weekday. Measured against the count taken at Start Day, a truck that reached every shop on its
/// Thursday round scored 21%. The round is the shops the truck is scheduled for on the weekday or has
/// called on before in the same area. See <see cref="VanDayPlans"/>.
/// </remarks>
public sealed class VanDayRoundTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Mate = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Upcountry = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string Town = "VAN009";
    private const string Country = "VAN014";

    // The report is asked for 1 to 7 October 2026. What a round holds comes from before it.
    private static readonly DateTime LastThursday = new(2026, 9, 24);
    private static readonly DateTime LastFriday = new(2026, 9, 25);
    private static readonly DateTime Thursday = new(2026, 10, 1);
    private static readonly DateTime Monday = new(2026, 10, 5);
    private static readonly DateTime Tuesday = new(2026, 10, 6);
    private static readonly DateTime Wednesday = new(2026, 10, 7);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RouteEntity _west1;
    private readonly RouteEntity _upc1;

    public VanDayRoundTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

        _context.Database.EnsureCreated();

        _west1 = new RouteEntity { Code = "WEST1", Name = "West 1 Truck", Territory = "Harare" };
        _upc1 = new RouteEntity { Code = "UPC1", Name = "Upc 1", Territory = "UPC" };
        _context.Routes.AddRange(_west1, _upc1);
        _context.SaveChanges();

        // A town truck's Thursday and Friday. The upcountry route has a cycle week and no weekday.
        _context.RouteStops.AddRange(
            new RouteStopEntity { RouteId = _west1.Id, Name = "Glenorah", DayOfWeek = DayOfWeek.Thursday, Sequence = 1 },
            new RouteStopEntity { RouteId = _west1.Id, Name = "Glenview", DayOfWeek = DayOfWeek.Friday, Sequence = 1 },
            new RouteStopEntity { RouteId = _upc1.Id, Name = "Masvingo", WeekNumber = 1, Sequence = 1 });

        _context.Users.AddRange(
            User(Rep, "west1.one", Town, _west1.Id),
            User(Mate, "west1.two", Town, _west1.Id),
            User(Upcountry, "upc1.one", Country, _upc1.Id));

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- What a round holds ---

    [Fact]
    public async Task A_town_trucks_round_is_the_shops_it_called_on_that_weekday_before()
    {
        // Last Thursday: two check-ins by one rep and a sale by the other with no check-in at all.
        AddVisit(Rep, LastThursday, "A");
        AddVisit(Rep, LastThursday, "B");
        AddSale(Mate, LastThursday, "C");
        // Last Friday is another area and another round.
        AddVisit(Rep, LastFriday, "X");
        // This Thursday: two of the three, and a shop nobody had called on before.
        AddDay(Rep, Thursday, roster: 286);
        AddVisit(Rep, Thursday, "a");
        AddVisit(Rep, Thursday, "C");
        AddVisit(Rep, Thursday, "NEW");
        await _context.SaveChangesAsync();

        var day = Day(await ReportAsync(), _west1, Thursday);

        Assert.Equal("Round", day.PlanBasis);
        Assert.Equal(3, day.PlannedCustomers);
        Assert.Equal(2, day.PlannedCallsMade);
        Assert.Equal(2d / 3, day.CallComplianceRate!.Value, 6);

        // The new shop is still a call. It is not a planned one.
        Assert.Equal(3, day.CustomersVisited);
    }

    /// <summary>A shop first called on today is a new customer, not a call that was planned.</summary>
    [Fact]
    public async Task A_days_own_calls_are_not_its_plan()
    {
        AddDay(Rep, Thursday, roster: 286);
        AddVisit(Rep, Thursday, "A");
        AddVisit(Rep, Thursday, "B");
        await _context.SaveChangesAsync();

        var day = Day(await ReportAsync(), _west1, Thursday);

        // Nothing before it says what the Thursday round holds, so it is measured against the list.
        Assert.Equal("List", day.PlanBasis);
        Assert.Equal(286, day.PlannedCustomers);
        Assert.Equal(2, day.PlannedCallsMade);
    }

    [Fact]
    public async Task A_scheduled_shop_is_due_whether_or_not_the_truck_ever_called_on_it()
    {
        AddVisit(Rep, LastThursday, "A");
        AddCustomer("S", scheduledOn: DayOfWeek.Thursday);
        AddCustomer("F", scheduledOn: DayOfWeek.Friday);
        AddDay(Rep, Thursday, roster: 286);
        AddVisit(Rep, Thursday, "A");
        await _context.SaveChangesAsync();

        var day = Day(await ReportAsync(), _west1, Thursday);

        // A from the round as worked and S from the schedule. F is Friday's.
        Assert.Equal(2, day.PlannedCustomers);
        Assert.Equal(1, day.PlannedCallsMade);
    }

    /// <summary>Closing a shop is something the office can do about a round that cannot be completed.</summary>
    [Fact]
    public async Task A_deactivated_shop_is_no_longer_held_against_the_round()
    {
        AddVisit(Rep, LastThursday, "A");
        AddVisit(Rep, LastThursday, "B");
        AddCustomer("B", active: false);
        AddDay(Rep, Thursday, roster: 286);
        AddVisit(Rep, Thursday, "A");
        await _context.SaveChangesAsync();

        var day = Day(await ReportAsync(), _west1, Thursday);

        Assert.Equal(1, day.PlannedCustomers);
        Assert.Equal(1d, day.CallComplianceRate);
    }

    [Fact]
    public async Task A_shop_last_called_on_more_than_thirteen_weeks_ago_has_left_the_round()
    {
        AddVisit(Rep, Thursday.AddDays(-7 * 13), "RECENT");
        AddVisit(Rep, Thursday.AddDays(-7 * 14), "LONGAGO");
        AddDay(Rep, Thursday, roster: 286);
        AddVisit(Rep, Thursday, "RECENT");
        await _context.SaveChangesAsync();

        var day = Day(await ReportAsync(), _west1, Thursday);

        Assert.Equal(1, day.PlannedCustomers);
    }

    // --- Upcountry ---

    /// <summary>
    /// An upcountry trip keeps no weekday, so a date cannot say where it is. Its round is known once
    /// the area is named, and the name is matched without regard to case, spaces or punctuation.
    /// </summary>
    [Fact]
    public async Task An_upcountry_day_has_a_round_once_its_area_is_named()
    {
        AddVisit(Upcountry, new DateTime(2026, 9, 21), "M1");
        AddVisit(Upcountry, new DateTime(2026, 9, 21), "M2");
        NameArea(_upc1, new DateTime(2026, 9, 21), "Masvingo");
        AddVisit(Upcountry, new DateTime(2026, 9, 22), "C1");
        NameArea(_upc1, new DateTime(2026, 9, 22), "Chivi");

        AddDay(Upcountry, Monday, roster: 34, route: _upc1);
        AddVisit(Upcountry, Monday, "M1");
        NameArea(_upc1, Monday, " masvingo. ");

        AddDay(Upcountry, Tuesday, roster: 34, route: _upc1);
        AddVisit(Upcountry, Tuesday, "M1");
        await _context.SaveChangesAsync();

        var report = await ReportAsync();
        var named = Day(report, _upc1, Monday);
        var unnamed = Day(report, _upc1, Tuesday);

        Assert.Equal(("Round", 2, 1), (named.PlanBasis, named.PlannedCustomers, named.PlannedCallsMade));

        // Nobody said where Tuesday was, so nothing says which shops it owed.
        Assert.Equal(("List", 34, 1), (unnamed.PlanBasis, unnamed.PlannedCustomers, unnamed.PlannedCallsMade));
    }

    // --- The reports agree ---

    /// <summary>
    /// A round is a handful of shops and a list is hundreds. Added together, one day on the list would
    /// put more into the bottom of the fraction than every round beside it.
    /// </summary>
    [Fact]
    public async Task A_period_is_measured_over_its_rounds_and_counts_the_days_that_had_none()
    {
        AddVisit(Rep, LastThursday, "A");
        AddVisit(Rep, LastThursday, "B");
        AddDay(Rep, Thursday, roster: 286);
        AddVisit(Rep, Thursday, "A");
        // A Wednesday with no stops and nothing before it: the whole list.
        AddDay(Rep, Wednesday, roster: 10);
        AddVisit(Rep, Wednesday, "W1");
        await _context.SaveChangesAsync();

        var west1 = (await ReportAsync()).Trucks.Single(truck => truck.RouteId == _west1.Id);

        Assert.Equal("Round", west1.PlanBasis);
        Assert.Equal(2, west1.PlannedCustomers);
        Assert.Equal(1, west1.CcrCustomersVisited);
        Assert.Equal(0.5, west1.CallComplianceRate);
        Assert.Equal(1, west1.DaysOnWholeList);
    }

    [Fact]
    public async Task A_period_with_no_round_at_all_falls_back_to_the_list_and_says_so()
    {
        AddDay(Rep, Wednesday, roster: 10);
        AddVisit(Rep, Wednesday, "W1");
        await _context.SaveChangesAsync();

        var west1 = (await ReportAsync()).Trucks.Single(truck => truck.RouteId == _west1.Id);

        Assert.Equal("List", west1.PlanBasis);
        Assert.Equal(10, west1.PlannedCustomers);
        Assert.Equal(0.1, west1.CallComplianceRate);
    }

    /// <summary>The compliance page and the truck report read one plan, so they cannot show two CCRs.</summary>
    [Fact]
    public async Task The_compliance_report_measures_the_same_round()
    {
        AddVisit(Rep, LastThursday, "A");
        AddVisit(Rep, LastThursday, "B");
        AddSale(Mate, LastThursday, "C");
        AddDay(Rep, Thursday, roster: 286);
        AddVisit(Rep, Thursday, "A");
        AddVisit(Rep, Thursday, "NEW");
        await _context.SaveChangesAsync();

        var result = await new GetDepartureComplianceReportHandler(
                _context,
                Options.Create(new TaxSettings()),
                new CartrackReadService(_context, Options.Create(new CartrackSettings())))
            .Handle(new GetDepartureComplianceReportQuery(Thursday, Thursday), CancellationToken.None);

        var row = Assert.Single(result.Value.Days);

        Assert.Equal("Round", row.PlanBasis);
        Assert.Equal(3, row.TruckPlannedCustomerCount);
        Assert.Equal(1, row.TruckCustomersVisited);
        Assert.Equal(1d / 3, row.CallComplianceRate!.Value, 6);

        // The rep's own figures are untouched: the list as counted, and every shop checked into.
        Assert.Equal(286, row.PlannedCustomerCount);
        Assert.Equal(2, row.CustomersVisited);
    }

    // --- The plan itself ---

    [Fact]
    public void An_area_is_the_same_area_however_it_is_typed()
    {
        Assert.Equal(VanDayPlans.AreaKey("Glen Norah B"), VanDayPlans.AreaKey(" glen norah b, "));
        Assert.NotEqual(VanDayPlans.AreaKey("Glenorah"), VanDayPlans.AreaKey("Glen Norah"));
    }

    // --- Helpers ---

    private async Task<VanDailyTruckReportResult> ReportAsync()
    {
        var result = await new GetVanDailyTruckReportHandler(_context, Options.Create(new TaxSettings()))
            .Handle(new GetVanDailyTruckReportQuery(Thursday, Wednesday), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private static VanDailyTruckDayDto Day(VanDailyTruckReportResult report, RouteEntity route, DateTime day) =>
        report.Days.Single(row => row.RouteId == route.Id && row.TradingDate == day);

    private static User User(Guid id, string username, string account, int routeId) => new()
    {
        Id = id,
        Username = username,
        Email = $"{username}@example.com",
        PasswordHash = "x",
        Role = "Sales",
        IsActive = true,
        AssignedBusinessPartnerCode = account,
        RouteId = routeId
    };

    private void AddDay(Guid rep, DateTime day, int roster, RouteEntity? route = null)
    {
        route ??= _west1;

        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = rep,
            Username = "rep",
            TradingDate = day,
            RouteId = route.Id,
            RouteCode = route.Code,
            RouteName = route.Name,
            DepartedAt = DateTime.SpecifyKind(day.AddHours(5).AddMinutes(30), DateTimeKind.Utc),
            PlannedCustomerCount = roster
        });
    }

    private void AddVisit(Guid rep, DateTime day, string shop)
    {
        // Spaced out and checked out again: a rep has one open call at a time, and the table says so.
        var at = DateTime.SpecifyKind(day.AddHours(6), DateTimeKind.Utc).AddMinutes(_visits++ * 11 % 600);

        _context.TimesheetEntries.Add(new TimesheetEntryEntity
        {
            UserId = rep,
            Username = "rep",
            Channel = TimesheetChannel.VanSales,
            CustomerCode = shop,
            CustomerName = shop,
            CheckInTime = at,
            CheckOutTime = at.AddMinutes(5)
        });
    }

    private int _visits;

    private void AddSale(Guid rep, DateTime day, string shop)
    {
        var at = DateTime.SpecifyKind(day.AddHours(8), DateTimeKind.Utc);

        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = Guid.NewGuid().ToString(),
            SourceSystem = "KefalosVanSales",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = Town,
            CardName = "Van 009",
            RouteCustomerCode = shop,
            RouteCustomerName = shop,
            TotalValue = 10m,
            Currency = "USD",
            PaymentMethod = "Cash",
            Status = ReservationStatus.Confirmed,
            CreatedAt = at,
            ExpiresAt = at.AddHours(1),
            ConfirmedAt = at,
            CreatedBy = rep.ToString()
        });
    }

    private void AddCustomer(string code, DayOfWeek? scheduledOn = null, bool active = true)
    {
        var customer = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = Town,
            Code = code,
            Name = code,
            IsActive = active
        };

        _context.RouteCustomers.Add(customer);
        _context.SaveChanges();

        if (scheduledOn is { } weekday)
        {
            _context.RouteCustomerVisitDays.Add(
                new RouteCustomerVisitDayEntity { RouteCustomerId = customer.Id, DayOfWeek = weekday });
        }
    }

    private void NameArea(RouteEntity route, DateTime day, string area)
    {
        _context.VanTruckDayEntries.Add(
            new VanTruckDayEntryEntity { RouteId = route.Id, TradingDate = day, AreaName = area });
    }
}
