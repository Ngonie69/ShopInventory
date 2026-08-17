using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanReplenishmentReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the van replenishment report.
///
/// Two things here are easy to get wrong in ways that flatter the depot. A missing timestamp counted
/// as an instant decision makes the service level look best exactly where the record is worst; and a
/// mean rather than a median lets one request left over a long weekend hide that everything else was
/// handled the same morning. Both are pinned below.
/// </summary>
public sealed class VanReplenishmentReportTests : IDisposable
{
    private const string Van = "KEFVAN10";
    private const string OtherVan = "KEFVAN11";
    private const string Depot = "KEFGRC";

    private static readonly DateTime From = new(2026, 8, 1);
    private static readonly DateTime To = new(2026, 8, 31);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanReplenishmentReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        AddVanUser("van010", Van);
        AddVanUser("van011", OtherVan);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// A van is a warehouse a rep is assigned to that has a supplying depot behind it. Matching on a
    /// code prefix would work against the repository's seed data and find nothing in production,
    /// where warehouses are alpha-coded and named for places.
    /// </summary>
    [Fact]
    public async Task Vans_are_found_from_their_user_assignment_not_a_code_prefix()
    {
        // A store warehouse: assigned to somebody, but with no depot supplying it.
        _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "shopkeeper",
            Email = "shop@example.com",
            PasswordHash = "x",
            Role = "Cashier",
            IsActive = true,
            AssignedWarehouseCode = "KEFSHOP"
        });
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Summary.VanCount);
        Assert.DoesNotContain(report.Vans, van => van.VanWarehouseCode == "KEFSHOP");
    }

    /// <summary>The two waits are different problems and are measured separately.</summary>
    [Fact]
    public async Task The_wait_for_a_decision_is_reported_apart_from_the_wait_to_post()
    {
        AddRequest("R1", Van, requestedAt: Utc(4, 6), decidedAt: Utc(4, 10), postedAt: Utc(4, 16));
        await _context.SaveChangesAsync();

        var van = Assert.Single((await RunAsync()).Vans, v => v.VanWarehouseCode == Van);

        Assert.Equal(4d, van.MedianHoursToDecision);
        Assert.Equal(10d, van.MedianHoursToPosting);
        Assert.Equal(1, van.PostedCount);
        Assert.Equal(1d, van.PostRate);
    }

    /// <summary>
    /// The middle wait, not the mean. One request left over a weekend would otherwise hide that
    /// every other one was decided within the hour.
    /// </summary>
    [Fact]
    public async Task The_waiting_figure_is_a_median_so_one_outlier_cannot_hide_the_rest()
    {
        AddRequest("R1", Van, Utc(3, 8), decidedAt: Utc(3, 9));
        AddRequest("R2", Van, Utc(4, 8), decidedAt: Utc(4, 9));
        AddRequest("R3", Van, Utc(5, 8), decidedAt: Utc(5, 9));
        // Left over a long weekend.
        AddRequest("R4", Van, Utc(6, 8), decidedAt: Utc(10, 8));
        await _context.SaveChangesAsync();

        var van = Assert.Single((await RunAsync()).Vans, v => v.VanWarehouseCode == Van);

        // The mean would be about 25 hours; three of the four were done in one.
        Assert.Equal(1d, van.MedianHoursToDecision);
    }

    /// <summary>
    /// A decided request with no decision time is excluded and counted, never treated as instant —
    /// that would make the service level look best exactly where the record is worst.
    /// </summary>
    [Fact]
    public async Task A_missing_timestamp_is_counted_rather_than_read_as_no_wait()
    {
        AddRequest("R1", Van, Utc(4, 8), decidedAt: Utc(4, 14));
        AddRequest("R2", Van, Utc(5, 8), decidedAt: null, status: PendingInventoryTransferStatuses.Approved);
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var van = Assert.Single(report.Vans, v => v.VanWarehouseCode == Van);

        Assert.Equal(6d, van.MedianHoursToDecision);
        Assert.Equal(1, report.Quality.RequestsWithoutDecisionTime);
        Assert.False(report.Quality.IsClean);
    }

    /// <summary>
    /// A failed post leads the worklist. It was decided and then lost, and nothing else in the system
    /// surfaces it — an approved transfer that never reached SAP just sits there.
    /// </summary>
    [Fact]
    public async Task A_failed_post_leads_the_worklist_ahead_of_one_merely_waiting()
    {
        AddRequest("WAITING", Van, Utc(2, 8), status: PendingInventoryTransferStatuses.AwaitingApproval);
        AddRequest("FAILED", Van, Utc(20, 8), decidedAt: Utc(20, 9),
            status: "PostFailed", lastError: "SAP connection closed");
        await _context.SaveChangesAsync();

        var worklist = (await RunAsync()).NeedingAttention;

        Assert.Equal(2, worklist.Count);
        Assert.True(worklist[0].IsPostFailure);
        Assert.Equal("SAP connection closed", worklist[0].LastError);

        // The one merely waiting has waited far longer, and still sorts second.
        Assert.True(worklist[1].HoursWaiting > worklist[0].HoursWaiting);
    }

    /// <summary>A period with no requests has no service level — not a perfect one.</summary>
    [Fact]
    public async Task A_van_that_asked_for_nothing_has_no_post_rate()
    {
        var report = await RunAsync();
        var van = Assert.Single(report.Vans, v => v.VanWarehouseCode == Van);

        Assert.Equal(0, van.RequestCount);
        Assert.Null(van.PostRate);
        Assert.Null(van.MedianHoursToDecision);
        Assert.Null(van.DaysSinceLastPosted);
        Assert.Equal(2, report.Quality.VansWithNoRequests);
    }

    /// <summary>
    /// A van that has never had a load posted is a different finding from one whose last load was a
    /// long time ago — one is badly served, the other has never been served.
    /// </summary>
    [Fact]
    public async Task A_van_never_supplied_is_distinguished_from_one_supplied_long_ago()
    {
        AddRequest("R1", Van, Utc(4, 8), decidedAt: Utc(4, 9), postedAt: Utc(4, 10));
        AddRequest("R2", OtherVan, Utc(5, 8), status: PendingInventoryTransferStatuses.AwaitingApproval);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.NotNull(Assert.Single(report.Vans, v => v.VanWarehouseCode == Van).DaysSinceLastPosted);
        Assert.Null(Assert.Single(report.Vans, v => v.VanWarehouseCode == OtherVan).DaysSinceLastPosted);
    }

    /// <summary>Requests for one van must not be counted against another.</summary>
    [Fact]
    public async Task Each_vans_requests_are_counted_against_that_van_only()
    {
        AddRequest("R1", Van, Utc(4, 8), decidedAt: Utc(4, 9), postedAt: Utc(4, 10));
        AddRequest("R2", OtherVan, Utc(4, 8));
        AddRequest("R3", OtherVan, Utc(5, 8));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, Assert.Single(report.Vans, v => v.VanWarehouseCode == Van).RequestCount);
        Assert.Equal(2, Assert.Single(report.Vans, v => v.VanWarehouseCode == OtherVan).RequestCount);
        Assert.Equal(3, report.Summary.RequestCount);
    }

    /// <summary>Filtering to one van answers for that van and no other.</summary>
    [Fact]
    public async Task One_vans_window_holds_only_that_van()
    {
        AddRequest("R1", Van, Utc(4, 8));
        AddRequest("R2", OtherVan, Utc(4, 8));
        await _context.SaveChangesAsync();

        var report = await RunAsync(vanWarehouseCode: Van);

        Assert.Equal(Van, Assert.Single(report.Vans).VanWarehouseCode);
        Assert.Equal(1, report.Summary.RequestCount);
    }

    /// <summary>A posted request with no SAP document cannot be confirmed against SAP.</summary>
    [Fact]
    public async Task A_post_with_no_sap_document_is_flagged()
    {
        AddRequest("R1", Van, Utc(4, 8), decidedAt: Utc(4, 9), postedAt: Utc(4, 10), sapDocNum: null);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Quality.PostedWithoutSapDocNum);
        Assert.Contains(report.Quality.Caveats, caveat => caveat.Contains("no SAP"));
    }

    [Fact]
    public async Task A_backwards_period_is_refused()
    {
        var handler = new GetVanReplenishmentReportHandler(_context);

        var result = await handler.Handle(
            new GetVanReplenishmentReportQuery(To, From),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidRange", result.FirstError.Code);
    }

    // --- Helpers ---

    private async Task<VanReplenishmentReportResult> RunAsync(string? vanWarehouseCode = null)
    {
        var handler = new GetVanReplenishmentReportHandler(_context);

        var result = await handler.Handle(
            new GetVanReplenishmentReportQuery(From, To, vanWarehouseCode),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    /// <summary>An instant on the given August day, in UTC.</summary>
    private static DateTime Utc(int day, int hour) => new(2026, 8, day, hour, 0, 0, DateTimeKind.Utc);

    private void AddVanUser(string username, string warehouse) =>
        _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = warehouse,
            // The depot behind it is what makes this warehouse a van rather than a store.
            SupplyingWarehouseCode = Depot,
            AssignedBusinessPartnerCode = warehouse
        });

    private void AddRequest(
        string reference,
        string vanWarehouse,
        DateTime requestedAt,
        DateTime? decidedAt = null,
        DateTime? postedAt = null,
        string? status = null,
        int? sapDocNum = 5001,
        string? lastError = null) =>
        _context.PendingInventoryTransfers.Add(new PendingInventoryTransferEntity
        {
            Id = Guid.NewGuid(),
            ClientRequestId = reference,
            FromWarehouse = Depot,
            ToWarehouse = vanWarehouse,
            PayloadJson = "{}",
            Status = status ?? (postedAt.HasValue
                ? PendingInventoryTransferStatuses.Posted
                : decidedAt.HasValue
                    ? PendingInventoryTransferStatuses.Approved
                    : PendingInventoryTransferStatuses.AwaitingApproval),
            CreatedByUserId = Guid.NewGuid(),
            CreatedByName = "Tinashe Moyo",
            CreatedAtUtc = requestedAt,
            DecidedAtUtc = decidedAt,
            PostedAtUtc = postedAt,
            LineCount = 8,
            TotalQuantity = 120m,
            SapDocNum = postedAt.HasValue ? sapDocNum : null,
            LastError = lastError
        });
}
