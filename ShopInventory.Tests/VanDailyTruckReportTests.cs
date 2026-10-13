using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Commands.SaveRoute;
using ShopInventory.Features.VanSalesReports.Commands.SaveVanTruckDayEntry;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetRoutes;
using ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The daily truck report, and the three things it needed that the system had nowhere to put: the unit
/// a truck's odometer counts in, the area a round worked, and the office's own figures for a truck's day.
/// </summary>
/// <remarks>
/// The report replaces a workbook the office typed by hand. Reconciled against production for the week
/// of 1 October 2026 the workbook's sales were right and three other things were not: the system read
/// 13.4% under it because online sales are stored before tax, three trucks' distances were 62% of what
/// they drove because their odometers read miles, and fuel, account sales and targets were in the
/// workbook and nowhere else.
/// </remarks>
public sealed class VanDailyTruckReportTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Mate = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Loose = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string VanAccount = "VAN009";

    // Thursday 1 October 2026 to Wednesday 7 October: five working days and a weekend.
    private static readonly DateTime Thursday = new(2026, 10, 1);
    private static readonly DateTime Friday = new(2026, 10, 2);
    private static readonly DateTime Saturday = new(2026, 10, 3);
    private static readonly DateTime Monday = new(2026, 10, 5);
    private static readonly DateTime Wednesday = new(2026, 10, 7);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RouteEntity _west1;
    private readonly RouteEntity _upc1;

    public VanDailyTruckReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

        _context.Database.EnsureCreated();

        _west1 = new RouteEntity
        {
            Code = "WEST1",
            Name = "West 1 Truck",
            Territory = "Harare",
            OdometerUnit = OdometerUnit.Miles,
            DailySalesTarget = 2500m
        };
        _upc1 = new RouteEntity { Code = "UPC1", Name = "Upc 1", Territory = "UPC", DailySalesTarget = 3000m };

        _context.Routes.AddRange(_west1, _upc1);
        _context.SaveChanges();

        _context.RouteStops.AddRange(
            Stop(_west1, "Glenorah", DayOfWeek.Thursday, sequence: 2),
            Stop(_west1, "Glen Norah B", DayOfWeek.Thursday, sequence: 1),
            Stop(_west1, "Budiriro", DayOfWeek.Thursday, sequence: 1, alternateSet: 1),
            Stop(_west1, "Glenview", DayOfWeek.Friday, sequence: 1),
            // An upcountry trip keeps a cycle week and no weekday, so no date places it.
            new RouteStopEntity { RouteId = _upc1.Id, Name = "Masvingo", WeekNumber = 1, Sequence = 1 });

        _context.Users.AddRange(
            User(Rep, "west1.one", VanAccount, _west1.Id),
            User(Mate, "west1.two", VanAccount, _west1.Id),
            User(Loose, "no.route", "VAN099", null));

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The odometer ---

    [Theory]
    [InlineData(OdometerUnit.Kilometres, 37)]
    [InlineData(OdometerUnit.Miles, 60)]
    public void Two_readings_become_kilometres_whatever_the_odometer_counts_in(OdometerUnit unit, int kilometres)
    {
        // West 1 on Thursday 1 October: 332644 to 332681. The workbook made it 59.5 km.
        Assert.Equal(kilometres, OdometerDistance.Kilometres(332644, 332681, unit));
    }

    [Fact]
    public void A_reading_that_ran_backwards_is_no_distance_at_all()
    {
        Assert.Null(OdometerDistance.Kilometres(332681, 332644, OdometerUnit.Miles));
        Assert.Null(OdometerDistance.Kilometres(332644, null, OdometerUnit.Miles));
    }

    [Fact]
    public async Task A_truck_that_reads_miles_is_reported_in_kilometres()
    {
        AddDay(Rep, Thursday, start: 332644, close: 332681, unit: OdometerUnit.Miles);
        await _context.SaveChangesAsync();

        var day = Day(await ReportAsync(), _west1, Thursday);

        Assert.Equal(332644, day.StartingMileage);
        Assert.Equal("Miles", day.OdometerUnit);
        Assert.Equal(60, day.Kilometres);
    }

    /// <summary>Both reps may read the odometer. Adding their readings would double the trip.</summary>
    [Fact]
    public async Task Two_reps_reading_one_odometer_is_one_trip()
    {
        AddDay(Rep, Thursday, start: 1000, close: 1100, unit: OdometerUnit.Kilometres);
        AddDay(Mate, Thursday, start: 1000, close: 1090, unit: OdometerUnit.Kilometres);
        await _context.SaveChangesAsync();

        Assert.Equal(100, Day(await ReportAsync(), _west1, Thursday).Kilometres);
    }

    // --- The area ---

    [Fact]
    public void A_weekday_plan_names_the_standard_stops_in_schedule_order()
    {
        var stops = _context.RouteStops.Where(stop => stop.RouteId == _west1.Id).AsEnumerable().Select(AsPlan).ToList();

        Assert.Equal("Glen Norah B, Glenorah", RouteDayPlan.AreaFor(stops, DayOfWeek.Thursday));
        Assert.Null(RouteDayPlan.AreaFor(stops, DayOfWeek.Monday));
    }

    [Fact]
    public void An_upcountry_trip_has_no_plan_a_date_can_place()
    {
        var stops = _context.RouteStops.Where(stop => stop.RouteId == _upc1.Id).AsEnumerable().Select(AsPlan).ToList();

        Assert.All(
            Enum.GetValues<DayOfWeek>(),
            day => Assert.Null(RouteDayPlan.AreaFor(stops, day)));
    }

    [Fact]
    public async Task The_office_then_the_day_then_the_plan_says_where_a_truck_worked()
    {
        AddDay(Rep, Friday, area: "Glenview and Highfield");
        _context.VanTruckDayEntries.Add(new VanTruckDayEntryEntity
        {
            RouteId = _west1.Id,
            TradingDate = Monday,
            AreaName = "Budiriro"
        });
        await _context.SaveChangesAsync();

        var report = await ReportAsync();

        Assert.Equal(("Glen Norah B, Glenorah", "Plan"), Area(Day(report, _west1, Thursday)));
        Assert.Equal(("Glenview and Highfield", "Day"), Area(Day(report, _west1, Friday)));
        Assert.Equal(("Budiriro", "Office"), Area(Day(report, _west1, Monday)));
    }

    // --- The rows ---

    /// <summary>A truck that reported nothing is the finding. Leaving its days out would hide it.</summary>
    [Fact]
    public async Task A_live_route_owes_a_row_for_every_working_day_and_none_for_a_weekend()
    {
        var report = await ReportAsync();
        var west1 = report.Days.Where(day => day.RouteId == _west1.Id).ToList();

        Assert.Equal(5, report.WorkingDays);
        Assert.Equal(5, west1.Count);
        Assert.DoesNotContain(west1, day => day.TradingDate == Saturday);
        Assert.All(west1, day => Assert.True(day.NotReported));
        Assert.Equal(10, report.Exceptions.NotReported);
    }

    [Fact]
    public async Task A_weekend_the_truck_traded_on_is_listed_and_owes_no_target()
    {
        AddSale(Rep, Saturday, "SHOP1", 100m);
        await _context.SaveChangesAsync();

        var saturday = Day(await ReportAsync(), _west1, Saturday);

        Assert.False(saturday.IsWorkingDay);
        Assert.Null(saturday.DailySalesTarget);
        Assert.False(saturday.BelowTarget);
        Assert.Equal(115.50m, saturday.Sales);
    }

    [Fact]
    public async Task Two_reps_on_one_truck_are_one_row()
    {
        AddDay(Rep, Thursday, planned: 40);
        AddDay(Mate, Thursday, planned: 40);
        AddVisit(Rep, Thursday, "SHOP1");
        AddVisit(Mate, Thursday, "SHOP2");
        AddVisit(Mate, Thursday, "SHOP3");
        AddSale(Rep, Thursday, "SHOP1", 100m, paymentMethod: "Cash");
        AddSale(Mate, Thursday, "SHOP4", 200m, paymentMethod: "Ecocash");
        await _context.SaveChangesAsync();

        var day = Day(await ReportAsync(), _west1, Thursday);

        // 300 before tax is 346.50 with it.
        Assert.Equal(346.50m, day.Sales);
        Assert.Equal(115.50m, day.Cash);
        Assert.Equal(231m, day.Ecocash);
        Assert.Equal(2, day.SalesCount);

        // One list of 40, three shops checked into, two that bought, one of them never checked into.
        Assert.Equal(40, day.PlannedCustomers);
        Assert.Equal(3, day.CustomersVisited);
        Assert.Equal(2, day.ProductiveCalls);
        Assert.Equal(4, day.CallsMade);
        Assert.Equal(0.075, day.CallComplianceRate!.Value, 3);
        Assert.Equal(0.5, day.ProductiveCallRate!.Value, 3);
        Assert.Equal(173.25m, day.AverageOrderValue);
        Assert.True(day.BelowTarget);
    }

    [Fact]
    public async Task A_day_closed_the_next_morning_says_so()
    {
        AddDay(Rep, Thursday, returnedAtUtc: new DateTime(2026, 10, 2, 6, 30, 0, DateTimeKind.Utc));
        await _context.SaveChangesAsync();

        var report = await ReportAsync();

        Assert.True(Day(report, _west1, Thursday).ReturnedLate);
        Assert.Equal(1, report.Exceptions.ReturnedLate);
    }

    [Fact]
    public async Task Distance_with_no_sales_is_picked_out()
    {
        AddDay(Rep, Friday, start: 1000, close: 1200, unit: OdometerUnit.Kilometres);
        await _context.SaveChangesAsync();

        var report = await ReportAsync();

        Assert.True(Day(report, _west1, Friday).DistanceWithoutSales);
        Assert.Equal(1, report.Exceptions.DistanceWithoutSales);
    }

    /// <summary>The rows are routes. Takings by a login on no route are in none of them, and are stated.</summary>
    [Fact]
    public async Task Takings_by_a_login_on_no_route_are_stated_not_dropped()
    {
        AddSale(Loose, Thursday, "ELSEWHERE", 100m);
        await _context.SaveChangesAsync();

        var report = await ReportAsync();

        Assert.Equal(115.50m, report.SalesOutsideRoutes);
        Assert.Equal(0m, report.Total.Sales);
    }

    // --- The period ---

    [Fact]
    public async Task A_periods_target_is_the_daily_target_times_its_working_days()
    {
        AddSale(Rep, Thursday, "SHOP1", 1000m);
        _context.VanTruckDayEntries.Add(new VanTruckDayEntryEntity
        {
            RouteId = _west1.Id,
            TradingDate = Monday,
            FuelLitres = 50m,
            AccountSales = 388m
        });
        AddDay(Rep, Monday, start: 1000, close: 1100, unit: OdometerUnit.Miles);
        await _context.SaveChangesAsync();

        var report = await ReportAsync();
        var west1 = report.Trucks.Single(truck => truck.RouteId == _west1.Id);

        Assert.Equal(12500m, west1.PeriodTarget);
        Assert.Equal(1155m, west1.Sales);
        Assert.Equal(11345m, west1.BalanceToTarget);
        Assert.Equal(0.0924, west1.TargetAchieved!.Value, 4);
        Assert.Equal(388m, west1.AccountSales);
        Assert.Equal(2, west1.DaysReported);

        // 100 miles is 161 km, on 50 litres.
        Assert.Equal(161, west1.Kilometres);
        Assert.Equal(3.22m, west1.KilometresPerLitre);

        // The fleet's target is every live route's: 2,500 and 3,000 a day over five days.
        Assert.Equal(27500m, report.Total.PeriodTarget);
        Assert.Equal(1155m, report.Total.Sales);
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        var result = await Handler().Handle(
            new GetVanDailyTruckReportQuery(Wednesday, Thursday), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanTruckDays.InvalidRange", result.FirstError.Code);
    }

    [Fact]
    public async Task A_period_longer_than_a_quarter_is_refused()
    {
        var result = await Handler().Handle(
            new GetVanDailyTruckReportQuery(Thursday, Thursday.AddDays(GetVanDailyTruckReportHandler.MaximumDays)),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanTruckDays.RangeTooWide", result.FirstError.Code);
    }

    // --- The office's entry ---

    [Fact]
    public async Task The_office_enters_a_day_once_and_corrects_it_in_place()
    {
        var first = await SaveEntryAsync(Thursday, area: " Glenorah ", fuel: 30m, account: 388m);
        var second = await SaveEntryAsync(Thursday, area: "Glenorah", fuel: 35m, account: 388m);

        Assert.False(first.IsError);
        Assert.Equal("Glenorah", first.Value.AreaName);
        Assert.Equal(35m, second.Value.FuelLitres);
        Assert.Equal(35m, Assert.Single(_context.VanTruckDayEntries.AsNoTracking().ToList()).FuelLitres);
    }

    [Fact]
    public async Task Saving_a_day_blank_removes_its_entry()
    {
        await SaveEntryAsync(Thursday, fuel: 30m);

        var cleared = await SaveEntryAsync(Thursday);

        Assert.False(cleared.Value.HasEntry);
        Assert.Empty(_context.VanTruckDayEntries.AsNoTracking().ToList());
    }

    [Theory]
    [InlineData(-1.0, null, "VanTruckDays.FuelOutOfRange")]
    [InlineData(2001.0, null, "VanTruckDays.FuelOutOfRange")]
    [InlineData(null, -0.01, "VanTruckDays.AccountSalesNegative")]
    public async Task A_figure_that_cannot_be_right_is_refused(double? fuel, double? account, string code)
    {
        var result = await SaveEntryAsync(Thursday, fuel: (decimal?)fuel, account: (decimal?)account);

        Assert.True(result.IsError);
        Assert.Equal(code, result.FirstError.Code);
    }

    [Fact]
    public async Task A_day_that_has_not_happened_is_refused()
    {
        var tomorrow = AuditService.ToCAT(DateTime.UtcNow).Date.AddDays(1);

        var result = await SaveEntryAsync(tomorrow, fuel: 30m);

        Assert.Equal("VanTruckDays.DayInFuture", result.FirstError.Code);
    }

    [Fact]
    public async Task A_route_that_is_gone_is_refused()
    {
        var result = await new SaveVanTruckDayEntryHandler(
                _context, new NoOpAuditService(), NullLogger<SaveVanTruckDayEntryHandler>.Instance)
            .Handle(new SaveVanTruckDayEntryCommand(9999, Thursday, null, 30m, null, null, null), CancellationToken.None);

        Assert.Equal("VanTruckDays.RouteNotFound", result.FirstError.Code);
    }

    // --- The route ---

    [Fact]
    public async Task A_route_keeps_its_odometer_unit_and_its_target()
    {
        var saved = await SaveRouteAsync(_upc1, odometerUnit: "miles", dailySalesTarget: 3200m);

        Assert.False(saved.IsError);
        Assert.Equal("Miles", saved.Value.OdometerUnit);
        Assert.Equal(3200m, saved.Value.DailySalesTarget);

        var listed = (await new GetRoutesHandler(_context).Handle(new GetRoutesQuery(), CancellationToken.None)).Value;
        Assert.Equal("Miles", listed.Single(route => route.Id == _upc1.Id).OdometerUnit);
    }

    /// <summary>A save that does not name a unit is an older page, and must not turn miles into kilometres.</summary>
    [Fact]
    public async Task A_save_that_names_no_unit_leaves_the_unit_alone()
    {
        var saved = await SaveRouteAsync(_west1, odometerUnit: null, dailySalesTarget: 2500m);

        Assert.Equal("Miles", saved.Value.OdometerUnit);
    }

    [Fact]
    public async Task A_target_of_nothing_is_no_target()
    {
        var saved = await SaveRouteAsync(_west1, odometerUnit: null, dailySalesTarget: 0m);

        Assert.Null(saved.Value.DailySalesTarget);
    }

    [Theory]
    [InlineData("furlongs", null, "Routes.OdometerUnitUnknown")]
    [InlineData("7", null, "Routes.OdometerUnitUnknown")]
    [InlineData(null, -1.0, "Routes.DailySalesTargetNegative")]
    public async Task A_unit_or_target_that_cannot_be_right_is_refused(string? unit, double? target, string code)
    {
        var result = await SaveRouteAsync(_west1, unit, (decimal?)target);

        Assert.True(result.IsError);
        Assert.Equal(code, result.FirstError.Code);
    }

    // --- Helpers ---

    private GetVanDailyTruckReportHandler Handler() => new(_context, Options.Create(new TaxSettings()));

    private async Task<VanDailyTruckReportResult> ReportAsync()
    {
        var result = await Handler().Handle(
            new GetVanDailyTruckReportQuery(Thursday, Wednesday), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private static VanDailyTruckDayDto Day(VanDailyTruckReportResult report, RouteEntity route, DateTime day) =>
        report.Days.Single(row => row.RouteId == route.Id && row.TradingDate == day);

    private static (string?, string?) Area(VanDailyTruckDayDto day) => (day.AreaName, day.AreaSource);

    private static RouteDayPlan.Stop AsPlan(RouteStopEntity stop) =>
        new(stop.RouteId, stop.Name, stop.DayOfWeek, stop.WeekNumber, stop.AlternateSet, stop.Sequence);

    private Task<ErrorOr.ErrorOr<VanTruckDayEntryDto>> SaveEntryAsync(
        DateTime day,
        string? area = null,
        decimal? fuel = null,
        decimal? account = null) =>
        new SaveVanTruckDayEntryHandler(_context, new NoOpAuditService(), NullLogger<SaveVanTruckDayEntryHandler>.Instance)
            .Handle(new SaveVanTruckDayEntryCommand(_west1.Id, day, area, fuel, account, null, Rep), CancellationToken.None);

    private Task<ErrorOr.ErrorOr<RouteDto>> SaveRouteAsync(RouteEntity route, string? odometerUnit, decimal? dailySalesTarget) =>
        new SaveRouteHandler(_context, new NoOpAuditService(), NullLogger<SaveRouteHandler>.Instance)
            .Handle(
                new SaveRouteCommand(
                    route.Id, route.Code, route.Name, route.Territory, null, null, null, null, true, Rep,
                    odometerUnit, dailySalesTarget),
                CancellationToken.None);

    private static RouteStopEntity Stop(RouteEntity route, string name, DayOfWeek day, int sequence, int alternateSet = 0) =>
        new() { RouteId = route.Id, Name = name, DayOfWeek = day, Sequence = sequence, AlternateSet = alternateSet };

    private static User User(Guid id, string username, string account, int? routeId) => new()
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

    private void AddDay(
        Guid rep,
        DateTime day,
        int? start = null,
        int? close = null,
        OdometerUnit unit = OdometerUnit.Kilometres,
        int planned = 0,
        string? area = null,
        DateTime? returnedAtUtc = null)
    {
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = rep,
            Username = "rep",
            TradingDate = day,
            RouteId = _west1.Id,
            RouteCode = _west1.Code,
            RouteName = _west1.Name,
            DepartedAt = DateTime.SpecifyKind(day.AddHours(5).AddMinutes(30), DateTimeKind.Utc),
            ReturnedAt = returnedAtUtc ?? (close.HasValue ? DateTime.SpecifyKind(day.AddHours(13), DateTimeKind.Utc) : null),
            StartingMileage = start,
            ClosingMileage = close,
            OdometerUnit = unit,
            PlannedCustomerCount = planned,
            AreaName = area
        });
    }

    private void AddVisit(Guid rep, DateTime day, string shop)
    {
        _context.TimesheetEntries.Add(new TimesheetEntryEntity
        {
            UserId = rep,
            Username = "rep",
            Channel = TimesheetChannel.VanSales,
            CustomerCode = shop,
            CustomerName = shop,
            CheckInTime = DateTime.SpecifyKind(day.AddHours(7), DateTimeKind.Utc),
            // Checked out again: a rep has one open call at a time, and the table says so.
            CheckOutTime = DateTime.SpecifyKind(day.AddHours(7).AddMinutes(10), DateTimeKind.Utc)
        });
    }

    /// <summary>An online sale as it is stored: before tax, on a reservation.</summary>
    private void AddSale(Guid rep, DateTime day, string shop, decimal net, string? paymentMethod = "Cash")
    {
        var at = DateTime.SpecifyKind(day.AddHours(8), DateTimeKind.Utc);

        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = Guid.NewGuid().ToString(),
            SourceSystem = "KefalosVanSales",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = VanAccount,
            CardName = "Van 009",
            RouteCustomerCode = shop,
            RouteCustomerName = shop,
            TotalValue = net,
            Currency = "USD",
            PaymentMethod = paymentMethod,
            Status = ReservationStatus.Confirmed,
            CreatedAt = at,
            ExpiresAt = at.AddHours(1),
            ConfirmedAt = at,
            CreatedBy = rep.ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar 1kg",
                    OriginalQuantity = 1m,
                    ReservedQuantity = 1m,
                    UoMCode = "EA",
                    WarehouseCode = "VAN009",
                    UnitPrice = net,
                    LineTotal = net
                }
            ]
        });
    }

    /// <summary>The audit trail is not what these tests are about, and it must not fail them.</summary>
    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole,
            string? entityType = null, string? entityId = null, string? details = null,
            string? endpoint = null, bool isSuccess = true, string? errorMessage = null) =>
            Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) =>
            Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) =>
            Task.CompletedTask;
    }
}
