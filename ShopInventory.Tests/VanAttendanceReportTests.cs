using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisitReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The van attendance report: calls and time on site, per rep, over CAT trading days.
///
/// Two things here are not shared with the merchandiser timesheet report and are the reason this
/// report exists separately rather than as a channel switch on that one.
///
/// The first is the day boundary. The merchandiser report groups on the raw UTC date; a van's calls
/// run into the evening, and CAT is UTC+2, so anything after 22:00 UTC belongs to the next trading
/// day. Grouping on UTC would file it a day early and put this report out of step with the departure
/// compliance sheet, which counts CAT trading days — a supervisor reading the two side by side would
/// see different call counts for the same week and have no way to tell which was right.
///
/// The second is the open call. A rep who checks in and drives off without checking out leaves a row
/// with no duration. Counting it as a zero-length call would shorten their average time on site,
/// which rewards the omission; the report counts those separately and averages only over the calls
/// that closed.
/// </summary>
public sealed class VanAttendanceReportTests : IDisposable
{
    private static readonly Guid VanRep = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherRep = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Merchandiser = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanAttendanceReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            GivenUser(VanRep, "van-rep", "Tendai", "Moyo", ApplicationRoles.Adr),
            GivenUser(OtherRep, "other-van", "Rudo", "Chikwe", ApplicationRoles.Adr),
            GivenUser(Merchandiser, "shelf-rep", "Anesu", "Dube", ApplicationRoles.Merchandiser));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── The day boundary ────────────────────────────────────────────────────

    /// <summary>
    /// 23:00 UTC on the 12th is 01:00 CAT on the 13th. The call belongs to the 13th's round.
    /// </summary>
    [Fact]
    public async Task A_call_after_ten_at_night_utc_belongs_to_the_next_trading_day()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 23, 00, 0, DateTimeKind.Utc), minutes: 20);

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 13));

        var rep = Assert.Single(report.RepSummaries);
        var day = Assert.Single(rep.Days);
        Assert.Equal(new DateTime(2026, 8, 13), day.Date);
    }

    /// <summary>
    /// The pair that would merge under UTC grouping: 21:00 and 23:00 UTC on the same date are two
    /// different CAT trading days, and a supervisor reads them as two rounds.
    /// </summary>
    [Fact]
    public async Task Two_calls_either_side_of_midnight_cat_are_two_trading_days()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 21, 00, 0, DateTimeKind.Utc), minutes: 15);
        GivenCall(VanRep, new DateTime(2026, 8, 12, 23, 00, 0, DateTimeKind.Utc), minutes: 15);

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 13));

        var rep = Assert.Single(report.RepSummaries);
        Assert.Equal(2, rep.TradingDays);
        Assert.Equal(2, report.TradingDays);
        Assert.Equal([new DateTime(2026, 8, 13), new DateTime(2026, 8, 12)], rep.Days.Select(d => d.Date));
    }

    /// <summary>
    /// The window is named in CAT too. A call at 22:30 UTC on the 11th is 00:30 CAT on the 12th and
    /// is inside a report asked for from the 12th — filtering on the UTC date would drop it.
    /// </summary>
    [Fact]
    public async Task The_window_opens_on_the_cat_day_not_the_utc_one()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 11, 22, 30, 0, DateTimeKind.Utc), minutes: 10);

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal(1, report.TotalCalls);
        Assert.Equal(new DateTime(2026, 8, 12), Assert.Single(Assert.Single(report.RepSummaries).Days).Date);
    }

    /// <summary>The whole of the closing day counts, not up to midnight UTC on it.</summary>
    [Fact]
    public async Task The_window_covers_the_whole_of_the_closing_trading_day()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 20, 00, 0, DateTimeKind.Utc), minutes: 10);

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal(1, report.TotalCalls);
    }

    // ── Open calls ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_open_call_is_counted_but_does_not_shorten_the_average()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 60);
        GivenOpenCall(VanRep, new DateTime(2026, 8, 12, 10, 00, 0, DateTimeKind.Utc));

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal(2, report.TotalCalls);
        Assert.Equal(1, report.CompletedCalls);
        Assert.Equal(1, report.OpenCalls);

        // 60 minutes over the one call that closed, not 30 over both.
        Assert.Equal(60, report.AverageCallMinutes);

        var rep = Assert.Single(report.RepSummaries);
        Assert.Equal(1, rep.OpenCalls);
        Assert.Equal(60, rep.AverageMinutesPerCall);
        Assert.Equal(1, Assert.Single(rep.Days).OpenCalls);
    }

    [Fact]
    public async Task A_rep_whose_every_call_is_open_reports_no_average_rather_than_a_wrong_one()
    {
        GivenOpenCall(VanRep, new DateTime(2026, 8, 12, 09, 00, 0, DateTimeKind.Utc));

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal(0, report.AverageCallMinutes);
        Assert.Equal(0, report.TotalHours);
        Assert.Equal(1, report.OpenCalls);
    }

    // ── Offline capture ─────────────────────────────────────────────────────

    /// <summary>
    /// A call recorded on the handset and synced hours later. Ordinary on a van and worth naming, so
    /// a supervisor knows the times came off a queued record rather than a live one.
    /// </summary>
    [Fact]
    public async Task A_call_synced_long_after_it_happened_is_counted_as_offline()
    {
        var occurred = new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc);
        GivenCall(VanRep, occurred, minutes: 20, recordedAt: occurred.AddHours(6));
        GivenCall(VanRep, occurred.AddHours(1), minutes: 20, recordedAt: occurred.AddHours(1).AddSeconds(3));

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal(1, report.OfflineCalls);
        Assert.Equal(1, Assert.Single(report.RepSummaries).OfflineCalls);
    }

    // ── Scope ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_report_never_counts_a_merchandiser_visit()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);
        GivenCall(Merchandiser, new DateTime(2026, 8, 12, 09, 00, 0, DateTimeKind.Utc), minutes: 45,
            channel: TimesheetChannel.Merchandiser);

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal(1, report.TotalCalls);
        Assert.Equal("van-rep", Assert.Single(report.RepSummaries).Username);
    }

    [Fact]
    public async Task Reps_are_ranked_by_calls_made()
    {
        GivenCall(OtherRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);
        GivenCall(VanRep, new DateTime(2026, 8, 12, 09, 00, 0, DateTimeKind.Utc), minutes: 30);

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal(["van-rep", "other-van"], report.RepSummaries.Select(r => r.Username));
    }

    [Fact]
    public async Task A_shop_called_on_twice_is_one_customer_and_two_calls()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30, customerCode: "SHOP1");
        GivenCall(VanRep, new DateTime(2026, 8, 12, 14, 00, 0, DateTimeKind.Utc), minutes: 20, customerCode: "SHOP1");

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        var rep = Assert.Single(report.RepSummaries);
        Assert.Equal(2, rep.TotalCalls);
        Assert.Equal(1, rep.DistinctCustomers);

        var customer = Assert.Single(rep.Customers);
        Assert.Equal(2, customer.CallCount);
        Assert.Equal(50, customer.TotalMinutes);
    }

    [Fact]
    public async Task The_rep_carries_their_full_name_for_the_page_to_show()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal("Tendai Moyo", Assert.Single(report.RepSummaries).FullName);
    }

    // ── The calls the attendance page draws ─────────────────────────────────
    //
    // The report used to answer in totals only, which is enough to tabulate a day and not enough to
    // draw one. The attendance page draws the strip — a block per call, and the gaps between them —
    // so each day carries its own calls.

    [Fact]
    public async Task Each_day_carries_its_calls_in_check_in_order()
    {
        // Added out of order, which is what a handset syncing a queued record does.
        GivenCall(VanRep, new DateTime(2026, 8, 12, 11, 00, 0, DateTimeKind.Utc), minutes: 20, customerCode: "SHOP3");
        GivenCall(VanRep, new DateTime(2026, 8, 12, 07, 00, 0, DateTimeKind.Utc), minutes: 15, customerCode: "SHOP1");
        GivenCall(VanRep, new DateTime(2026, 8, 12, 09, 00, 0, DateTimeKind.Utc), minutes: 25, customerCode: "SHOP2");

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        var day = Assert.Single(Assert.Single(report.RepSummaries).Days);
        Assert.Equal(["SHOP1", "SHOP2", "SHOP3"], day.Calls.Select(call => call.CustomerCode));
    }

    /// <summary>
    /// A call never checked out keeps a null check-out rather than borrowing its check-in. The page
    /// draws it as a stub with a ring round it; a zero-length block would vanish from the strip, and
    /// the gap after it would be charged to the rep as idle time.
    /// </summary>
    [Fact]
    public async Task An_open_call_reaches_the_page_with_no_check_out()
    {
        GivenOpenCall(VanRep, new DateTime(2026, 8, 12, 09, 00, 0, DateTimeKind.Utc));

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        var call = Assert.Single(Assert.Single(Assert.Single(report.RepSummaries).Days).Calls);
        Assert.Null(call.CheckOutTime);
        Assert.Equal(new DateTime(2026, 8, 12, 09, 00, 0, DateTimeKind.Utc), call.CheckInTime);
    }

    /// <summary>Calls follow their own trading day, not the rep's whole period.</summary>
    [Fact]
    public async Task Calls_are_carried_on_the_day_they_belong_to()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 20, customerCode: "SHOP1");
        GivenCall(VanRep, new DateTime(2026, 8, 13, 08, 00, 0, DateTimeKind.Utc), minutes: 20, customerCode: "SHOP2");

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 13));

        var rep = Assert.Single(report.RepSummaries);
        Assert.Equal(
            ["SHOP2", "SHOP1"],
            rep.Days.Select(day => Assert.Single(day.Calls).CustomerCode));
    }

    // ── The round each day ran on ───────────────────────────────────────────

    [Fact]
    public async Task A_day_carries_the_route_its_round_was_started_on()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);
        GivenRound(VanRep, new DateTime(2026, 8, 12), "GRV", "Guruve");

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        var day = Assert.Single(Assert.Single(report.RepSummaries).Days);
        Assert.Equal("GRV", day.RouteCode);
        Assert.Equal("Guruve", day.RouteName);
    }

    /// <summary>
    /// A rep who checked into customers without starting a day on the handset has calls and no
    /// round. Null, not an invented route — the page says "Route not recorded", which is a finding.
    /// </summary>
    [Fact]
    public async Task A_day_with_no_round_started_has_no_route()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        var rep = Assert.Single(report.RepSummaries);
        Assert.Null(Assert.Single(rep.Days).RouteName);
        Assert.Null(rep.RouteName);
    }

    /// <summary>
    /// A rep moved between routes mid-period is listed under the one they are running now, while
    /// each day keeps the route it was actually run on. The snapshot is the point: re-deriving the
    /// route from the rep's current assignment would rewrite every day they have ever worked.
    /// </summary>
    [Fact]
    public async Task A_rep_who_changed_routes_is_listed_under_the_latest_one()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);
        GivenCall(VanRep, new DateTime(2026, 8, 13, 08, 00, 0, DateTimeKind.Utc), minutes: 30);
        GivenRound(VanRep, new DateTime(2026, 8, 12), "GRV", "Guruve");
        GivenRound(VanRep, new DateTime(2026, 8, 13), "MTR", "Mutare");

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 13));

        var rep = Assert.Single(report.RepSummaries);
        Assert.Equal("Mutare", rep.RouteName);
        Assert.Equal(
            [("MTR", new DateTime(2026, 8, 13)), ("GRV", new DateTime(2026, 8, 12))],
            rep.Days.Select(day => (day.RouteCode, day.Date)));
    }

    /// <summary>One rep's round must never be stamped on another's day.</summary>
    [Fact]
    public async Task A_round_belongs_to_the_rep_who_started_it()
    {
        GivenCall(VanRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);
        GivenCall(OtherRep, new DateTime(2026, 8, 12, 08, 00, 0, DateTimeKind.Utc), minutes: 30);
        GivenRound(VanRep, new DateTime(2026, 8, 12), "GRV", "Guruve");

        var report = await WhenReportRun(from: new DateTime(2026, 8, 12), to: new DateTime(2026, 8, 12));

        Assert.Equal("Guruve", report.RepSummaries.Single(rep => rep.Username == "van-rep").RouteName);
        Assert.Null(report.RepSummaries.Single(rep => rep.Username == "other-van").RouteName);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<VanVisitReportResult> WhenReportRun(DateTime from, DateTime to)
    {
        _context.ChangeTracker.Clear();

        var result = await new GetVanVisitReportHandler(_context).Handle(
            new GetVanVisitReportQuery(from, to, null, null),
            default);

        Assert.False(result.IsError);
        return result.Value;
    }

    private void GivenCall(
        Guid userId,
        DateTime checkInUtc,
        double minutes,
        string customerCode = "SHOP1",
        DateTime? recordedAt = null,
        TimesheetChannel channel = TimesheetChannel.VanSales)
    {
        Add(userId, checkInUtc, checkInUtc.AddMinutes(minutes), minutes, customerCode, recordedAt, channel);
    }

    private void GivenOpenCall(Guid userId, DateTime checkInUtc, string customerCode = "SHOP1")
    {
        Add(userId, checkInUtc, null, null, customerCode, null, TimesheetChannel.VanSales);
    }

    /// <summary>
    /// The rep's Start Day on the handset, which is where the route on a day comes from. Keyed on
    /// the CAT trading day, like the report's own grouping.
    /// </summary>
    private void GivenRound(Guid userId, DateTime tradingDate, string routeCode, string routeName)
    {
        var username = _context.Users.AsNoTracking().Single(user => user.Id == userId).Username;

        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = userId,
            Username = username,
            TradingDate = tradingDate.Date,
            RouteCode = routeCode,
            RouteName = routeName,
            DepartedAt = tradingDate.Date.AddHours(5)
        });

        _context.SaveChanges();
    }

    private void Add(
        Guid userId,
        DateTime checkInUtc,
        DateTime? checkOutUtc,
        double? durationMinutes,
        string customerCode,
        DateTime? recordedAt,
        TimesheetChannel channel)
    {
        var username = _context.Users.AsNoTracking().Single(user => user.Id == userId).Username;

        _context.TimesheetEntries.Add(new TimesheetEntryEntity
        {
            Channel = channel,
            UserId = userId,
            Username = username,
            CustomerCode = customerCode,
            CustomerName = $"Customer {customerCode}",
            CheckInTime = checkInUtc,
            CheckOutTime = checkOutUtc,
            DurationMinutes = durationMinutes,
            // Live capture unless the test says otherwise: recorded the instant it happened.
            CheckInRecordedAt = recordedAt ?? checkInUtc,
            CheckOutRecordedAt = checkOutUtc
        });

        _context.SaveChanges();
    }

    private static User GivenUser(Guid id, string username, string firstName, string lastName, string role) => new()
    {
        Id = id,
        Username = username,
        FirstName = firstName,
        LastName = lastName,
        PasswordHash = "not-a-real-hash",
        Role = role
    };

    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options);
}
