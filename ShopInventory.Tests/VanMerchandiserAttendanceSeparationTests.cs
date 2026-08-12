using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.Timesheets.Commands.CheckIn;
using ShopInventory.Features.Timesheets.Commands.CheckOut;
using ShopInventory.Features.Timesheets.Queries.GetActiveCheckIn;
using ShopInventory.Features.Timesheets.Queries.GetTimesheetReport;
using ShopInventory.Features.Timesheets.Queries.GetTimesheets;
using ShopInventory.Features.VanSalesAttendance.Commands.VanCheckIn;
using ShopInventory.Features.VanSalesAttendance.Commands.VanCheckOut;
using ShopInventory.Features.VanSalesAttendance.Queries.GetActiveVanVisit;
using ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisits;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Holds the wall between merchandiser timesheets and van sales attendance.
///
/// The two operations shared one table, one command, one query and one endpoint, discriminated by a
/// channel argument that every caller chose for itself. The ones that forgot it mixed the two
/// silently: the timesheet report totalled van calls under the heading "Merchandisers", and a sales
/// rep — who supervises merchandisers and has no part in the vans — could read a van's day from the
/// list endpoint by naming the channel in the query string.
///
/// The fix was structural: separate commands, handlers, queries, endpoints and permissions, each
/// pinned to its own channel with no argument to pass. These tests exist because that guarantee is
/// invisible at a call site — nothing about writing <c>db.TimesheetEntries.Where(...)</c> in a new
/// handler warns you that you have just rejoined the two halves. Every test below fails the moment
/// one side can see the other's rows.
/// </summary>
public sealed class VanMerchandiserAttendanceSeparationTests : IDisposable
{
    private static readonly Guid Merchandiser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid VanRep = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanMerchandiserAttendanceSeparationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            GivenUser(Merchandiser, "shelf-rep", ApplicationRoles.Merchandiser),
            GivenUser(VanRep, "van-rep", ApplicationRoles.Adr));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Writes land in their own channel ────────────────────────────────────

    [Fact]
    public async Task A_merchandiser_check_in_is_stamped_merchandiser()
    {
        await WhenMerchandiserChecksIn("SHOP1");

        var entry = Assert.Single(_context.TimesheetEntries);
        Assert.Equal(TimesheetChannel.Merchandiser, entry.Channel);
    }

    [Fact]
    public async Task A_van_check_in_is_stamped_van_sales()
    {
        await WhenVanChecksIn("SHOP1");

        var entry = Assert.Single(_context.TimesheetEntries);
        Assert.Equal(TimesheetChannel.VanSales, entry.Channel);
    }

    // ── Reads cannot cross ──────────────────────────────────────────────────

    [Fact]
    public async Task The_merchandiser_list_never_returns_a_van_call()
    {
        await GivenOneVisitOfEach();

        var result = await new GetTimesheetsHandler(_context).Handle(
            new GetTimesheetsQuery(1, 50, null, null, null, null, null),
            default);

        var entry = Assert.Single(result.Value.Entries);
        Assert.Equal("shelf-rep", entry.Username);
    }

    [Fact]
    public async Task The_van_list_never_returns_a_merchandiser_visit()
    {
        await GivenOneVisitOfEach();

        var result = await new GetVanVisitsHandler(_context).Handle(
            new GetVanVisitsQuery(1, 50, null, null, null, null, null),
            default);

        var entry = Assert.Single(result.Value.Entries);
        Assert.Equal("van-rep", entry.Username);
    }

    /// <summary>
    /// The report took no channel at all and filtered on none, so it counted both operations and
    /// printed the total under "Merchandisers". A sales rep opening it read van calls as shelf visits.
    /// </summary>
    [Fact]
    public async Task The_timesheet_report_counts_merchandiser_visits_only()
    {
        await GivenOneVisitOfEach();

        var result = await new GetTimesheetReportHandler(_context).Handle(
            new GetTimesheetReportQuery(null, null, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)),
            default);

        Assert.Equal(1, result.Value.TotalVisits);
        var summary = Assert.Single(result.Value.UserSummaries);
        Assert.Equal("shelf-rep", summary.Username);
    }

    // ── One rep's open visit is invisible to the other side ─────────────────

    [Fact]
    public async Task The_merchandiser_active_lookup_does_not_see_an_open_van_call()
    {
        await WhenVanChecksIn("SHOP1");

        var result = await new GetActiveCheckInHandler(_context).Handle(
            new GetActiveCheckInQuery(VanRep),
            default);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task The_van_active_lookup_does_not_see_an_open_merchandiser_visit()
    {
        await WhenMerchandiserChecksIn("SHOP1");

        var result = await new GetActiveVanVisitHandler(_context).Handle(
            new GetActiveVanVisitQuery(Merchandiser),
            default);

        Assert.True(result.IsError);
    }

    // ── Neither check-out reaches across ────────────────────────────────────

    /// <summary>
    /// The sharp end of the split. Both handlers close "the user's open visit"; before the pin, one
    /// operation's check-out could close the other's call and write its duration onto the wrong row.
    /// Contrived on real data — a user holds one role — but it is the invariant the whole design
    /// rests on, and it costs one test to prove rather than to assume.
    /// </summary>
    [Fact]
    public async Task A_van_check_out_leaves_a_merchandiser_visit_by_the_same_user_open()
    {
        await WhenMerchandiserChecksIn("SHOP1");

        var result = await new VanCheckOutHandler(_context, AuditSink(), NullLogger<VanCheckOutHandler>.Instance)
            .Handle(new VanCheckOutCommand(Merchandiser, "shelf-rep", null, null, null), default);

        Assert.True(result.IsError);
        Assert.All(_context.TimesheetEntries, entry => Assert.Null(entry.CheckOutTime));
    }

    [Fact]
    public async Task A_merchandiser_check_out_leaves_a_van_call_by_the_same_user_open()
    {
        await WhenVanChecksIn("SHOP1");

        var result = await new CheckOutHandler(_context, AuditSink(), NullLogger<CheckOutHandler>.Instance)
            .Handle(new CheckOutCommand(VanRep, "van-rep", null, null, null), default);

        Assert.True(result.IsError);
        Assert.All(_context.TimesheetEntries, entry => Assert.Null(entry.CheckOutTime));
    }

    // ── Replay guards stay within their own channel ─────────────────────────

    /// <summary>
    /// Client references are unique across the table, so a van replay must not be answered with a
    /// merchandiser row that happens to carry the same reference — it would return another rep's
    /// customer to the handset as though the call had been recorded.
    /// </summary>
    [Fact]
    public async Task A_van_replay_is_not_satisfied_by_a_merchandiser_row()
    {
        await WhenMerchandiserChecksIn("SHOP1", clientReference: "ref-1");

        var result = await new VanCheckInHandler(_context, AuditSink(), NullLogger<VanCheckInHandler>.Instance)
            .Handle(
                new VanCheckInCommand(
                    VanRep, "van-rep", "SHOP2", "Second shop", null, null, null,
                    new ShopInventory.Common.Mobile.CaptureContext(ClientReference: "ref-2")),
                default);

        Assert.False(result.IsError);
        Assert.Equal("SHOP2", result.Value.CustomerCode);
        Assert.Equal(2, _context.TimesheetEntries.Count());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task GivenOneVisitOfEach()
    {
        await WhenMerchandiserChecksIn("SHOP1");
        await WhenVanChecksIn("SHOP2");
        _context.ChangeTracker.Clear();
    }

    private async Task WhenMerchandiserChecksIn(string customerCode, string? clientReference = null)
    {
        var result = await new CheckInHandler(_context, AuditSink(), NullLogger<CheckInHandler>.Instance)
            .Handle(
                new CheckInCommand(
                    Merchandiser, "shelf-rep", customerCode, $"Customer {customerCode}", null, null, null,
                    clientReference is null
                        ? null
                        : new ShopInventory.Common.Mobile.CaptureContext(ClientReference: clientReference)),
                default);

        Assert.False(result.IsError);
    }

    private async Task WhenVanChecksIn(string customerCode, string? clientReference = null)
    {
        var result = await new VanCheckInHandler(_context, AuditSink(), NullLogger<VanCheckInHandler>.Instance)
            .Handle(
                new VanCheckInCommand(
                    VanRep, "van-rep", customerCode, $"Customer {customerCode}", null, null, null,
                    clientReference is null
                        ? null
                        : new ShopInventory.Common.Mobile.CaptureContext(ClientReference: clientReference)),
                default);

        Assert.False(result.IsError);
    }

    private static IAuditService AuditSink() => new NoOpAuditService();

    private static User GivenUser(Guid id, string username, string role) => new()
    {
        Id = id,
        Username = username,
        PasswordHash = "not-a-real-hash",
        Role = role
    };

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null)
            => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }

    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options);
}
