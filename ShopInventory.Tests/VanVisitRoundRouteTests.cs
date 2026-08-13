using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisits;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The route and truck a van call belongs to.
///
/// /van-sales/activity groups calls into rounds — one rep, one CAT trading day — and names each round
/// by the van it ran in. That name is not on the call: it is snapshotted onto the rep's
/// <see cref="VanRouteDayEntity"/> when they start the day, so the query has to find it by matching
/// the call's trading day against the day the rep opened.
///
/// Three ways that can go wrong, all of them silent. It can read the rep's *current* route, which
/// rewrites history whenever someone is moved between routes. It can match on the raw UTC date, which
/// puts an evening call on the next rep-day and labels it with the wrong van. And it can leak — one
/// rep's route onto another rep's calls on the same date. Each test below is one of those.
/// </summary>
public sealed class VanVisitRoundRouteTests : IDisposable
{
    private static readonly Guid Tinashe = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Rudo = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // 21:30 UTC on 11 Aug is 23:30 on the 11th in CAT — a late call that still belongs to that day.
    private static readonly DateTime LateOnTheEleventh = new(2026, 8, 11, 21, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime TheEleventh = new(2026, 8, 11);
    private static readonly DateTime TheTwelfth = new(2026, 8, 12);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanVisitRoundRouteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            GivenUser(Tinashe, "tinashe", ApplicationRoles.Adr),
            GivenUser(Rudo, "rudo", ApplicationRoles.Adr));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_call_carries_the_route_of_the_day_it_was_made_on()
    {
        GivenDay(Tinashe, "tinashe", TheEleventh, "HN", "Harare North", "AEE 4412");
        GivenCall(Tinashe, "tinashe", LateOnTheEleventh);

        var visit = await WhenTheListIsRead();

        Assert.Equal("HN", visit.RouteCode);
        Assert.Equal("Harare North", visit.RouteName);
        Assert.Equal("AEE 4412", visit.TruckRegNo);
    }

    /// <summary>
    /// The call is at 21:30 UTC, which is the 11th in CAT and the 11th in UTC — but only just. Match on
    /// the UTC date and a call an hour later lands on the 12th and comes back wearing the wrong van;
    /// this pins the 21:30 case, and <see cref="A_late_evening_call_belongs_to_the_CAT_day_it_was_made_on"/>
    /// pins the one that crosses.
    /// </summary>
    [Fact]
    public async Task A_call_on_a_day_the_rep_never_started_carries_no_route()
    {
        GivenDay(Tinashe, "tinashe", TheTwelfth, "HN", "Harare North", "AEE 4412");
        GivenCall(Tinashe, "tinashe", LateOnTheEleventh);

        var visit = await WhenTheListIsRead();

        // Null, not the neighbouring day's van. A rep who checked into customers without starting a
        // day is a finding the page shows as "Route not recorded", and borrowing yesterday's route
        // would hide it.
        Assert.Null(visit.RouteCode);
        Assert.Null(visit.RouteName);
        Assert.Null(visit.TruckRegNo);
    }

    [Fact]
    public async Task A_late_evening_call_belongs_to_the_CAT_day_it_was_made_on()
    {
        // 23:10 UTC on the 11th is 01:10 on the 12th in CAT: the rep is still out on the 12th's round.
        var pastMidnightInCat = new DateTime(2026, 8, 11, 23, 10, 0, DateTimeKind.Utc);

        GivenDay(Tinashe, "tinashe", TheEleventh, "HN", "Harare North", "AEE 4412");
        GivenDay(Tinashe, "tinashe", TheTwelfth, "HS", "Harare South", "ADK 7108");
        GivenCall(Tinashe, "tinashe", pastMidnightInCat);

        var visit = await WhenTheListIsRead();

        Assert.Equal("Harare South", visit.RouteName);
        Assert.Equal("ADK 7108", visit.TruckRegNo);
    }

    [Fact]
    public async Task One_reps_route_never_lands_on_another_reps_call()
    {
        GivenDay(Rudo, "rudo", TheEleventh, "HS", "Harare South", "ADK 7108");
        GivenCall(Tinashe, "tinashe", LateOnTheEleventh);

        var visit = await WhenTheListIsRead();

        Assert.Null(visit.RouteName);
        Assert.Null(visit.TruckRegNo);
    }

    /// <summary>
    /// The snapshot wins over the route master. Renaming a route or moving a rep off it must not
    /// rewrite what last month's rounds ran as — the same reason the entity snapshots at all.
    /// </summary>
    [Fact]
    public async Task The_snapshot_is_returned_rather_than_the_current_route_record()
    {
        var route = new RouteEntity { Code = "HN", Name = "Harare North (renamed)", TruckRegNo = "NEW 0001" };
        _context.Routes.Add(route);
        _context.SaveChanges();

        GivenDay(Tinashe, "tinashe", TheEleventh, "HN", "Harare North", "AEE 4412", route.Id);
        GivenCall(Tinashe, "tinashe", LateOnTheEleventh);

        var visit = await WhenTheListIsRead();

        Assert.Equal("Harare North", visit.RouteName);
        Assert.Equal("AEE 4412", visit.TruckRegNo);
    }

    [Fact]
    public async Task Every_call_of_a_round_carries_the_same_van()
    {
        // Only the last call may be open: a partial unique index allows a rep exactly one active
        // check-in, which is the same rule the handset enforces at the door.
        GivenDay(Tinashe, "tinashe", TheEleventh, "HN", "Harare North", "AEE 4412");
        GivenCall(Tinashe, "tinashe", new DateTime(2026, 8, 11, 6, 0, 0, DateTimeKind.Utc), "SHOP1", closed: true);
        GivenCall(Tinashe, "tinashe", new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc), "SHOP2", closed: true);
        GivenCall(Tinashe, "tinashe", LateOnTheEleventh, "SHOP3");

        var result = await new GetVanVisitsHandler(_context).Handle(
            new GetVanVisitsQuery(1, 50, null, null, null, null, null), default);

        Assert.Equal(3, result.Value.Entries.Count);
        Assert.All(result.Value.Entries, entry => Assert.Equal("AEE 4412", entry.TruckRegNo));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<VanVisitDto> WhenTheListIsRead()
    {
        _context.ChangeTracker.Clear();

        var result = await new GetVanVisitsHandler(_context).Handle(
            new GetVanVisitsQuery(1, 50, null, null, null, null, null), default);

        Assert.False(result.IsError);
        return Assert.Single(result.Value.Entries);
    }

    private void GivenDay(
        Guid userId, string username, DateTime tradingDate,
        string code, string name, string truck, int? routeId = null)
    {
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = userId,
            Username = username,
            TradingDate = tradingDate,
            RouteId = routeId,
            RouteCode = code,
            RouteName = name,
            Territory = "UPC",
            TruckRegNo = truck,
            DepartedAt = tradingDate.AddHours(4),
            PlannedCustomerCount = 12
        });

        _context.SaveChanges();
    }

    private void GivenCall(
        Guid userId, string username, DateTime checkInUtc,
        string customerCode = "SHOP1", bool closed = false)
    {
        _context.TimesheetEntries.Add(new TimesheetEntryEntity
        {
            UserId = userId,
            Username = username,
            Channel = TimesheetChannel.VanSales,
            CustomerCode = customerCode,
            CustomerName = $"Customer {customerCode}",
            CheckInTime = checkInUtc,
            CheckOutTime = closed ? checkInUtc.AddMinutes(20) : null,
            DurationMinutes = closed ? 20 : null
        });

        _context.SaveChanges();
    }

    private static User GivenUser(Guid id, string username, string role) => new()
    {
        Id = id,
        Username = username,
        PasswordHash = "not-a-real-hash",
        Role = role
    };

    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options);
}
