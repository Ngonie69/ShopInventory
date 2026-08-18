using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesScorecardReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the period scorecard.
///
/// A scorecard has two ways to lie that its inputs do not, and these are written around both.
///
/// The first is the comparison. Every figure on this page is a difference between two windows, so a
/// prior window assembled even slightly differently turns an ordinary week into news. The handler
/// builds both windows through one method for that reason, and several of these tests exist to keep
/// it that way.
///
/// The second is the band. A colour is an accusation, and the data behind it is frequently missing:
/// a rep whose handset did not sync has no strike rate at all. Colouring that red would blame
/// somebody for a sync failure, so it must stay uncoloured, and the tests below pin that harder than
/// they pin the bands that are earned.
/// </summary>
public sealed class VanSalesScorecardReportTests : IDisposable
{
    private const string VanAccount = "VAN010";
    private const string OtherAccount = "VAN011";

    private static readonly Guid Rep = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OtherRep = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>The reported window: the week of 10-16 August 2026. The week before it is the comparison.</summary>
    private static readonly DateTime From = new(2026, 8, 10);
    private static readonly DateTime To = new(2026, 8, 16);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesScorecardReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        AddUser(Rep, "van010", VanAccount);
        AddUser(OtherRep, "van011", OtherAccount);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The comparison window ---

    /// <summary>
    /// The prior window is the equal-length window ending the day before this one starts. Getting it
    /// wrong by a day would overlap the two and damp every movement towards zero.
    /// </summary>
    [Fact]
    public async Task The_prior_period_is_the_equal_length_window_immediately_before()
    {
        var report = await RunAsync();

        Assert.Equal(new DateTime(2026, 8, 9), report.PriorToDate);
        Assert.Equal(new DateTime(2026, 8, 3), report.PriorFromDate);
        Assert.Equal(
            (report.ToDate - report.FromDate).Days,
            (report.PriorToDate - report.PriorFromDate).Days);
    }

    /// <summary>
    /// A rep who traded last week and not this one is the row a scorecard most needs to show. Taking
    /// the rows from the current window alone would drop them silently, which is the one direction
    /// this page must never fail in.
    /// </summary>
    [Fact]
    public async Task A_rep_who_stopped_trading_still_gets_a_row()
    {
        AddOutlet(VanAccount, "SHOP1", "Shop One");

        // Last week only.
        AddRouteDay(Rep, new DateTime(2026, 8, 5), planned: 10);
        AddVisit(Rep, "SHOP1", new DateTime(2026, 8, 5));
        AddSale(Rep, "OLD", "SHOP1", 100m, new DateTime(2026, 8, 5));
        await _context.SaveChangesAsync();

        var row = Assert.Single((await RunAsync()).Rows);

        Assert.Empty(row.TakingsByCurrency);
        Assert.Equal(100m, Assert.Single(row.PriorTakingsByCurrency).Gross);

        var movement = Assert.Single(row.TakingsMovement);
        Assert.Null(movement.Gross);
        Assert.Equal(100m, movement.PriorGross);
        // No current figure means no difference — not a fall to zero, which would be a claim.
        Assert.Null(movement.Movement);
    }

    /// <summary>
    /// A currency the fleet has just started taking has not grown infinitely; it has no comparison.
    /// A percentage here would be a division by zero wearing a percent sign.
    /// </summary>
    [Fact]
    public async Task A_currency_with_no_prior_trading_has_a_movement_but_no_percentage()
    {
        AddOutlet(VanAccount, "SHOP1", "Shop One");

        AddSale(Rep, "OLD", "SHOP1", 100m, new DateTime(2026, 8, 5));
        AddSale(Rep, "NEW", "SHOP1", 150m, new DateTime(2026, 8, 12));
        AddSale(Rep, "ZIG", "SHOP1", 400m, new DateTime(2026, 8, 12), currency: "ZWG");
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var usd = report.TakingsMovement.Single(row => row.Currency == "USD");
        Assert.Equal(50m, usd.Movement);
        Assert.Equal(0.5, usd.PercentChange);

        var zig = report.TakingsMovement.Single(row => row.Currency == "ZWG");
        Assert.Equal(400m, zig.Gross);
        Assert.Null(zig.PriorGross);
        Assert.Null(zig.Movement);
        Assert.Null(zig.PercentChange);
    }

    /// <summary>
    /// With nothing before it, every movement is unavailable rather than a rise from nothing, and
    /// the page says so on its own face.
    /// </summary>
    [Fact]
    public async Task An_empty_prior_period_says_so_rather_than_reporting_growth()
    {
        AddOutlet(VanAccount, "SHOP1", "Shop One");
        AddSale(Rep, "NEW", "SHOP1", 150m, new DateTime(2026, 8, 12));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.True(report.Quality.PriorPeriodEmpty);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("no van trading at all"));
        Assert.All(report.TakingsMovement, row => Assert.Null(row.Movement));
    }

    // --- The band ---

    /// <summary>
    /// No call records means no strike rate, and no strike rate means no colour. This is the test
    /// that stops the page blaming a rep for a handset that did not sync.
    /// </summary>
    [Fact]
    public async Task A_rep_with_no_calls_recorded_is_unrated_rather_than_red()
    {
        AddOutlet(VanAccount, "SHOP1", "Shop One");

        // Sales but no visit rows and no departure record.
        AddSale(Rep, "S1", "SHOP1", 100m, new DateTime(2026, 8, 12));
        await _context.SaveChangesAsync();

        var row = Assert.Single((await RunAsync()).Rows);

        Assert.Null(row.Calls);
        Assert.Null(row.StrikeRate);
        Assert.Equal(VanSalesScorecardBand.Unrated, row.Band);
        Assert.Contains((await RunAsync()).Quality.Caveats, caveat => caveat.Contains("no band"));
    }

    [Theory]
    // Four of four calls productive against a 0.75 target: at target.
    [InlineData(4, 4, VanSalesScorecardBand.Green)]
    // Three of four is 75%, exactly the target.
    [InlineData(4, 3, VanSalesScorecardBand.Green)]
    // Two of three is 67% — under target but inside ten points.
    [InlineData(3, 2, VanSalesScorecardBand.Amber)]
    // One of four is 25%, far under.
    [InlineData(4, 1, VanSalesScorecardBand.Red)]
    public async Task A_row_bands_on_how_far_under_target_it_is(
        int calls,
        int productive,
        VanSalesScorecardBand expected)
    {
        var day = new DateTime(2026, 8, 12);

        for (var index = 0; index < calls; index++)
        {
            AddOutlet(VanAccount, $"SHOP{index}", $"Shop {index}");
            AddVisit(Rep, $"SHOP{index}", day);
        }

        for (var index = 0; index < productive; index++)
        {
            AddSale(Rep, $"S{index}", $"SHOP{index}", 40m, day);
        }

        await _context.SaveChangesAsync();

        // No departure record, so call compliance is unavailable and the band rests on strike rate.
        var row = Assert.Single((await RunAsync()).Rows);

        Assert.Equal(expected, row.Band);
    }

    /// <summary>
    /// Money never enters the band. A route billing in ZiG and one billing in USD hold no position
    /// against each other, so a row taking a fortune is banded exactly like one taking little.
    /// </summary>
    [Fact]
    public async Task Takings_do_not_change_a_band()
    {
        var day = new DateTime(2026, 8, 12);

        AddOutlet(VanAccount, "SHOP1", "Shop One");
        AddOutlet(OtherAccount, "SHOP2", "Shop Two");

        AddVisit(Rep, "SHOP1", day);
        AddSale(Rep, "TINY", "SHOP1", 1m, day);

        AddVisit(OtherRep, "SHOP2", day);
        AddSale(OtherRep, "HUGE", "SHOP2", 999_999m, day, account: OtherAccount, username: "van011");
        await _context.SaveChangesAsync();

        var rows = (await RunAsync()).Rows;

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(VanSalesScorecardBand.Green, row.Band));
    }

    // --- Call compliance ---

    /// <summary>
    /// The same rule the coverage report had to be corrected for: a day whose plan reads zero is the
    /// handset's failed count, not a plan of none. It is out of the denominator, so its calls have to
    /// be out of the numerator, or the rate reads over 100%.
    /// </summary>
    [Fact]
    public async Task Call_compliance_counts_only_the_calls_on_days_that_stated_a_plan()
    {
        AddOutlet(VanAccount, "SHOP1", "Shop One");

        AddRouteDay(Rep, new DateTime(2026, 8, 11), planned: 10);
        AddVisit(Rep, "SHOP1", new DateTime(2026, 8, 11));
        AddVisit(Rep, "SHOP2", new DateTime(2026, 8, 11));

        // A day whose plan failed. Its eight calls must not count against the other day's plan.
        AddRouteDay(Rep, new DateTime(2026, 8, 12), planned: 0);
        foreach (var index in Enumerable.Range(0, 8))
        {
            AddVisit(Rep, $"OTHER{index}", new DateTime(2026, 8, 12));
        }

        await _context.SaveChangesAsync();

        var row = Assert.Single((await RunAsync()).Rows);

        Assert.Equal(10, row.PlannedCalls);
        Assert.Equal(10, row.Calls);
        Assert.Equal(2, row.CallsAgainstPlan);
        Assert.Equal(0.2, row.CallComplianceRate);
    }

    // --- Grouping by route ---

    /// <summary>
    /// A sale whose rep never opened a departure record carries nothing saying which route it was
    /// made on. Dropping it would make the route rows add up to less than the fleet with nothing on
    /// the page to explain the difference, so it gets its own row.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_departure_record_gets_its_own_route_row()
    {
        AddOutlet(VanAccount, "SHOP1", "Shop One");

        AddRouteDay(Rep, new DateTime(2026, 8, 11), planned: 5);
        AddSale(Rep, "ONROUTE", "SHOP1", 100m, new DateTime(2026, 8, 11));

        // No departure record for this day.
        AddSale(Rep, "ORPHAN", "SHOP1", 60m, new DateTime(2026, 8, 13));
        await _context.SaveChangesAsync();

        var rows = (await RunAsync(grouping: VanSalesScorecardGrouping.Route)).Rows;

        Assert.Equal(2, rows.Count);

        var orphan = rows.Single(row => row.Label == "No departure record");
        Assert.Equal(60m, Assert.Single(orphan.TakingsByCurrency).Gross);

        var onRoute = rows.Single(row => row.Label == "Guruve");
        Assert.Equal(100m, Assert.Single(onRoute.TakingsByCurrency).Gross);

        // And the two rows account for every penny the fleet took.
        Assert.Equal(160m, rows.Sum(row => row.TakingsByCurrency.Sum(total => total.Gross)));
    }

    // --- Movement ---

    /// <summary>
    /// A rate movement is in percentage points, not as a proportion of a proportion. A rep going
    /// from 50% to 75% has improved by twenty-five points, and reporting that as +50% would be a
    /// different and much larger-sounding claim.
    /// </summary>
    [Fact]
    public async Task A_rate_movement_is_in_percentage_points()
    {
        // Last week: two calls, one productive. 50%.
        AddOutlet(VanAccount, "A", "A");
        AddOutlet(VanAccount, "B", "B");
        AddVisit(Rep, "A", new DateTime(2026, 8, 5));
        AddVisit(Rep, "B", new DateTime(2026, 8, 5));
        AddSale(Rep, "OLD", "A", 40m, new DateTime(2026, 8, 5));

        // This week: four calls, three productive. 75%.
        foreach (var index in Enumerable.Range(0, 4))
        {
            AddOutlet(VanAccount, $"C{index}", $"C{index}");
            AddVisit(Rep, $"C{index}", new DateTime(2026, 8, 12));
        }

        foreach (var index in Enumerable.Range(0, 3))
        {
            AddSale(Rep, $"NEW{index}", $"C{index}", 40m, new DateTime(2026, 8, 12));
        }

        await _context.SaveChangesAsync();

        var row = Assert.Single((await RunAsync()).Rows);

        Assert.Equal(0.75, row.StrikeRate);
        Assert.Equal(0.5, row.PriorStrikeRate);
        Assert.Equal(0.25, row.StrikeRateMovement!.Value, 10);
    }

    // --- Validation ---

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_rejected()
    {
        var result = await RunRawAsync(From, From.AddDays(-1));

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidRange", result.FirstError.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    public async Task A_target_outside_zero_to_one_is_rejected(double target)
    {
        var result = await RunRawAsync(From, To, strikeRateTarget: target);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidTarget", result.FirstError.Code);
    }

    /// <summary>The roll-up says out loud that it is a summary and never an authority.</summary>
    [Fact]
    public async Task The_page_states_that_the_reports_it_summarises_are_the_authority()
    {
        var report = await RunAsync();

        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("the report is right and this is a bug"));
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("never ranked"));
    }

    // --- Harness ---

    private async Task<VanSalesScorecardReportResult> RunAsync(
        VanSalesScorecardGrouping grouping = VanSalesScorecardGrouping.Rep)
    {
        var result = await RunRawAsync(From, To, grouping);

        Assert.False(result.IsError);
        return result.Value;
    }

    private Task<ErrorOr.ErrorOr<VanSalesScorecardReportResult>> RunRawAsync(
        DateTime from,
        DateTime to,
        VanSalesScorecardGrouping grouping = VanSalesScorecardGrouping.Rep,
        double strikeRateTarget = 0.75) =>
        new GetVanSalesScorecardReportHandler(_context).Handle(
            new GetVanSalesScorecardReportQuery(
                from,
                to,
                grouping,
                UserId: null,
                CallComplianceTarget: 0.95,
                StrikeRateTarget: strikeRateTarget),
            CancellationToken.None);

    private void AddUser(Guid id, string username, string account) =>
        _context.Users.Add(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = account,
            AssignedBusinessPartnerCode = account
        });

    private void AddOutlet(string account, string code, string name)
    {
        if (_context.RouteCustomers.Local.Any(customer =>
                customer.AssignedBusinessPartnerCode == account && customer.Code == code))
        {
            return;
        }

        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = account,
            Code = code,
            Name = name,
            IsActive = true,
            CreatedAt = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc)
        });
    }

    private void AddRouteDay(Guid userId, DateTime tradingDate, int planned) =>
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = userId,
            Username = "van010",
            TradingDate = tradingDate,
            RouteCode = "GURUVE",
            RouteName = "Guruve",
            Territory = "Mash Central",
            DepartedAt = tradingDate.AddHours(5),
            PlannedCustomerCount = planned
        });

    private void AddVisit(Guid userId, string customerCode, DateTime tradingDate) =>
        _context.TimesheetEntries.Add(new TimesheetEntryEntity
        {
            Channel = TimesheetChannel.VanSales,
            UserId = userId,
            Username = "van010",
            CustomerCode = customerCode,
            CustomerName = customerCode,
            // 09:00 CAT, comfortably inside the trading day either way.
            CheckInTime = tradingDate.AddHours(7),
            CheckOutTime = tradingDate.AddHours(7).AddMinutes(20),
            CheckInLatitude = -17.8,
            CheckInLongitude = 31.05,
            CheckInLocationSource = TimesheetLocationSources.Gps,
            CheckInLocationAccuracyMetres = 15
        });

    private void AddSale(
        Guid userId,
        string reference,
        string? routeCustomerCode,
        decimal total,
        DateTime docDate,
        string account = VanAccount,
        string currency = "USD",
        string username = "van010") =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            CardCode = account,
            CardName = account,
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerCode is null ? null : $"Shop {routeCustomerCode}",
            DocDate = docDate,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            WarehouseCode = account,
            PaymentMethod = "Cash",
            AmountPaid = total,
            CreatedBy = userId.ToString(),
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Item CHE011",
                    Quantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = account
                }
            ]
        });
}
