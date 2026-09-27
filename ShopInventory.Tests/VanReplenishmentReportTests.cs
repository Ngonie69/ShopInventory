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
    /// A van is a warehouse a rep is assigned to that has a supplying depot behind it.
    ///
    /// Production vans are in fact coded <c>VAN0nn</c>, so a prefix filter would work today — which
    /// is exactly why this test uses codes that are not. The prefix is a convention; the assignment
    /// is the definition, and a store coded <c>VAN…</c> or a van coded anything else has to be
    /// classified on what it is rather than on what it is called.
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
        Assert.Equal(1d, van.FilledWithinDayRate);
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
    /// A failed post leads the worklist ahead of a request merely waiting on an approver: it was
    /// decided and then lost, and the van is going without while it sits.
    /// </summary>
    [Fact]
    public async Task A_failed_post_leads_the_worklist_ahead_of_one_merely_waiting()
    {
        AddRequest("WAITING", Van, Utc(2, 8), status: PendingInventoryTransferStatuses.AwaitingApproval);
        AddRequest("FAILED", Van, Utc(20, 8), decidedAt: Utc(20, 9),
            status: "PostFailed", lastError: "SAP connection closed");
        await _context.SaveChangesAsync();

        var worklist = (await RunAsync()).Unfilled;

        Assert.Equal(2, worklist.Count);
        Assert.Equal(VanReplenishmentCauses.PostRefused, worklist[0].Cause);
        Assert.Equal(VanReplenishmentCauses.AwaitingDecision, worklist[1].Cause);
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
        Assert.Null(van.FilledWithinDayRate);
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

    // ── What the review found ───────────────────────────────────────────────────

    /// <summary>
    /// A request stuck since before the period still needs somebody. Filtering the worklist by the
    /// period hid exactly the oldest ones.
    /// </summary>
    [Fact]
    public async Task A_request_stuck_since_before_the_period_is_still_in_the_worklist()
    {
        AddRequest("OLD", Van, July(20, 8), decidedAt: July(20, 9),
            status: PendingInventoryTransferStatuses.PostFailed, lastError: ShortageError(("YOG100", 360, 0)));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(0, report.Summary.RequestCount);
        var stuck = Assert.Single(report.Unfilled);
        Assert.Equal(VanReplenishmentCauses.DepotShort, stuck.Cause);
        Assert.Equal(1, report.Summary.UnfilledNowCount);
    }

    /// <summary>
    /// An approval whose post never ran reads Approved forever. It used to be counted nowhere —
    /// not posted, not failed, not waiting — so nothing showed it.
    /// </summary>
    [Fact]
    public async Task An_approval_whose_post_never_ran_is_in_the_worklist()
    {
        AddRequest("STRANDED", Van, Utc(10, 8), decidedAt: Utc(10, 9), status: PendingInventoryTransferStatuses.Approved);
        AddRequest("POSTING", Van, DateTime.UtcNow.AddMinutes(-5), decidedAt: DateTime.UtcNow.AddMinutes(-2),
            status: PendingInventoryTransferStatuses.Approved);
        await _context.SaveChangesAsync();

        var causes = (await RunAsync()).Unfilled.Select(request => request.Cause).ToList();

        Assert.Contains(VanReplenishmentCauses.ApprovedNeverPosted, causes);
        Assert.Contains(VanReplenishmentCauses.Posting, causes);
    }

    /// <summary>
    /// A request its requester took back before anybody decided was never owed; one withdrawn after it
    /// failed to post is a van that went without. The first leaves the service level, the second
    /// counts against it.
    /// </summary>
    [Fact]
    public async Task A_withdrawal_after_a_failed_post_counts_against_the_service_level_and_a_requester_cancel_does_not()
    {
        AddRequest("FILLED", Van, Utc(4, 8), decidedAt: Utc(4, 9), postedAt: Utc(4, 10));
        AddRequest("TAKEN-BACK", Van, Utc(5, 8), decidedAt: Utc(5, 9), status: PendingInventoryTransferStatuses.Cancelled);
        AddRequest("WENT-WITHOUT", Van, Utc(6, 8), decidedAt: Utc(6, 9), status: PendingInventoryTransferStatuses.Cancelled,
            withdrawnAt: Utc(12, 8));
        AddRequest("TURNED-DOWN", Van, Utc(7, 8), decidedAt: Utc(7, 9), status: PendingInventoryTransferStatuses.Rejected);
        await _context.SaveChangesAsync();

        var summary = (await RunAsync()).Summary;

        Assert.Equal(4, summary.RequestCount);
        Assert.Equal(2, summary.FillBase);
        Assert.Equal(0.5, summary.FilledWithinDayRate);
        Assert.Equal(1, summary.WithdrawnAfterFailureCount);
        Assert.Equal(1, summary.CancelledCount);
    }

    /// <summary>
    /// The waits cover every request, not only the ones that got there — a median over the posted ones
    /// alone read "under an hour" while a van had waited three weeks.
    /// </summary>
    [Fact]
    public async Task The_wait_bands_account_for_every_request_raised()
    {
        AddRequest("FAST", Van, Utc(4, 8), decidedAt: Utc(4, 8), postedAt: Utc(4, 8).AddMinutes(30));
        AddRequest("SLOW", Van, Utc(4, 8), decidedAt: Utc(4, 9), postedAt: Utc(8, 8));
        AddRequest("STUCK", Van, Utc(5, 8), decidedAt: Utc(5, 9), status: PendingInventoryTransferStatuses.PostFailed,
            lastError: ShortageError(("YOG100", 10, 0)));
        AddRequest("NO", Van, Utc(6, 8), decidedAt: Utc(6, 9), status: PendingInventoryTransferStatuses.Rejected);
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var bands = report.Waits.ToDictionary(band => band.Band, band => band.Count);

        Assert.Equal(report.Summary.RequestCount, report.Waits.Sum(band => band.Count));
        Assert.Equal(1, bands[VanReplenishmentWaitBands.UnderOneHour]);
        Assert.Equal(1, bands[VanReplenishmentWaitBands.OverThreeDays]);
        Assert.Equal(1, bands[VanReplenishmentWaitBands.StillOpen]);
        Assert.Equal(1, bands[VanReplenishmentWaitBands.TurnedDown]);
    }

    /// <summary>
    /// A van last loaded the day before the period has been served, just not in these dates. Reading
    /// only the period called it "never".
    /// </summary>
    [Fact]
    public async Task A_load_before_the_period_is_still_the_vans_last_load()
    {
        AddRequest("JULY", Van, July(28, 8), decidedAt: July(28, 9), postedAt: July(28, 10));
        await _context.SaveChangesAsync();

        var van = Assert.Single((await RunAsync()).Vans, v => v.VanWarehouseCode == Van);

        Assert.Equal(0, van.RequestCount);
        Assert.NotNull(van.LastPostedAt);
        Assert.True(van.LastPostedBeforePeriod);
    }

    /// <summary>
    /// A van nobody drives any more earns no row saying it asked for nothing — but a request of its
    /// still open is still somebody's job.
    /// </summary>
    [Fact]
    public async Task A_deactivated_reps_van_drops_from_the_idle_list_but_not_from_the_worklist()
    {
        AddVanUser("retired", "KEFVAN99", isActive: false);
        AddVanUser("retired2", "KEFVAN98", isActive: false);
        AddRequest("LEFT", "KEFVAN98", July(2, 8), status: PendingInventoryTransferStatuses.AwaitingApproval);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.DoesNotContain(report.Vans, van => van.VanWarehouseCode is "KEFVAN99" or "KEFVAN98");
        Assert.Equal("KEFVAN98", Assert.Single(report.Unfilled).VanWarehouseCode);
        Assert.Equal(2, report.Quality.VansWithNoRequests);
    }

    /// <summary>
    /// The depot's shortfall is added up across every request it cannot fill, so it reads as one
    /// restock list rather than one long error per van.
    /// </summary>
    [Fact]
    public async Task A_depots_shortages_are_added_up_across_the_requests_it_cannot_fill()
    {
        AddRequest("A", Van, Utc(4, 8), decidedAt: Utc(4, 9), status: PendingInventoryTransferStatuses.PostFailed,
            lastError: ShortageError(("YOG100", 360, 0), ("YOG017", 34, 0)), lineCount: 25);
        AddRequest("B", OtherVan, Utc(5, 8), decidedAt: Utc(5, 9), status: PendingInventoryTransferStatuses.PostFailed,
            lastError: ShortageError(("YOG100", 240, 0)), lineCount: 10);
        await _context.SaveChangesAsync();

        var report = await RunAsync();
        var depot = Assert.Single(report.DepotShortages);

        Assert.Equal(Depot, depot.DepotWarehouseCode);
        Assert.Equal(2, depot.RequestCount);
        Assert.Equal(2, depot.VanCount);
        Assert.Equal(35, depot.LineCount);
        Assert.Equal(32, depot.LinesInStock);
        var yoghurt = depot.Items[0];
        Assert.Equal("YOG100", yoghurt.ItemCode);
        Assert.Equal(600, yoghurt.Shortage);
        Assert.Equal(2, yoghurt.RequestCount);

        var first = Assert.Single(report.Unfilled, request => request.VanWarehouseCode == Van);
        Assert.Equal(23, first.LinesInStockAtLastAttempt);
    }

    [Fact]
    public async Task A_timed_out_post_is_read_as_an_unknown_outcome_and_leads_the_worklist()
    {
        AddRequest("SHORT", Van, Utc(2, 8), decidedAt: Utc(2, 9), status: PendingInventoryTransferStatuses.PostFailed,
            lastError: ShortageError(("YOG100", 10, 0)));
        AddRequest("TIMEOUT", Van, Utc(20, 8), decidedAt: Utc(20, 9), status: PendingInventoryTransferStatuses.PostFailed,
            lastError: "The SAP post timed out before SAP answered, so it is not known whether the transfer was created. Check SAP for this transfer before retrying — retrying will post it again.");
        await _context.SaveChangesAsync();

        var worklist = (await RunAsync()).Unfilled;

        Assert.Equal(VanReplenishmentCauses.OutcomeUnknown, worklist[0].Cause);
        Assert.Equal(VanReplenishmentCauses.DepotShort, worklist[1].Cause);
    }

    /// <summary>
    /// Filtering to one depot answers for its requests and the idle vans it supplies, and the filter's
    /// own choices stay whole — the van list used to shrink to the one van picked.
    /// </summary>
    [Fact]
    public async Task One_depots_window_holds_its_requests_and_the_choices_stay_whole()
    {
        _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "byo",
            Email = "byo@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = "KEFVAN20",
            SupplyingWarehouseCode = "KEFBYC",
            AssignedBusinessPartnerCode = "KEFVAN20"
        });
        AddRequest("GRC", Van, Utc(4, 8));
        AddRequest("BYC", "KEFVAN20", Utc(4, 8), fromWarehouse: "KEFBYC");
        await _context.SaveChangesAsync();

        var handler = new GetVanReplenishmentReportHandler(_context);
        var result = await handler.Handle(
            new GetVanReplenishmentReportQuery(From, To, DepotWarehouseCode: "KEFBYC"), CancellationToken.None);

        Assert.False(result.IsError);
        var report = result.Value;
        Assert.Equal(1, report.Summary.RequestCount);
        Assert.Equal("KEFVAN20", Assert.Single(report.Vans).VanWarehouseCode);
        Assert.Equal([Van, OtherVan, "KEFVAN20"], report.AvailableVans);
        Assert.Equal(["KEFBYC", Depot], report.AvailableDepots);

        var oneVan = await RunAsync(vanWarehouseCode: Van);
        Assert.Equal(3, oneVan.AvailableVans.Count);
    }

    private static DateTime July(int day, int hour) => new(2026, 7, day, hour, 0, 0, DateTimeKind.Utc);

    private static string ShortageError(params (string Item, decimal Requested, decimal Available)[] lines) =>
        "Insufficient stock in source warehouse: " + string.Join("; ", lines.Select(line => new ShopInventory.DTOs.StockValidationError
        {
            ItemCode = line.Item,
            WarehouseCode = Depot,
            RequestedQuantity = line.Requested,
            AvailableQuantity = line.Available
        }.Message));


    // --- Helpers ---

    private async Task<VanReplenishmentReportResult> RunAsync(string? vanWarehouseCode = null, DateTime? to = null)
    {
        var handler = new GetVanReplenishmentReportHandler(_context);

        var result = await handler.Handle(
            new GetVanReplenishmentReportQuery(From, to ?? To, vanWarehouseCode),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    /// <summary>An instant on the given August day, in UTC.</summary>
    private static DateTime Utc(int day, int hour) => new(2026, 8, day, hour, 0, 0, DateTimeKind.Utc);

    private void AddVanUser(string username, string warehouse, bool isActive = true) =>
        _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = isActive,
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
        string? lastError = null,
        DateTime? withdrawnAt = null,
        DateTime? lastAttemptedAt = null,
        int lineCount = 8,
        string fromWarehouse = Depot) =>
        _context.PendingInventoryTransfers.Add(new PendingInventoryTransferEntity
        {
            Id = Guid.NewGuid(),
            ClientRequestId = reference,
            FromWarehouse = fromWarehouse,
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
            LineCount = lineCount,
            TotalQuantity = 120m,
            SapDocNum = postedAt.HasValue ? sapDocNum : null,
            LastError = lastError,
            WithdrawnAtUtc = withdrawnAt,
            LastAttemptedAtUtc = lastAttemptedAt
        });
}
