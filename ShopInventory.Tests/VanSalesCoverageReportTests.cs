using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesCoverageReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the coverage and outlet development report.
///
/// The churn arithmetic is what these mostly guard. Four movements have to partition cleanly — a
/// shop cannot be new and reactivated at once, and lapsing has to be counted at the boundary it
/// happened on rather than every month afterwards — and if they do not, the identity
/// <c>Closing = Opening + New + Reactivated − Lapsed</c> stops holding. The report publishes that
/// residual on every bucket precisely so a regression here shows on the page; these make it show in
/// the build instead.
/// </summary>
public sealed class VanSalesCoverageReportTests : IDisposable
{
    private const string VanAccount = "VAN010";
    private const string OtherAccount = "VAN011";

    private static readonly Guid Rep = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OtherRep = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>The window every case is written around: the whole of August 2026.</summary>
    private static readonly DateTime From = new(2026, 8, 1);
    private static readonly DateTime To = new(2026, 8, 31);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesCoverageReportTests()
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

    // --- The churn identity ---

    /// <summary>
    /// The residual has to be zero on every bucket. It is published rather than asserted internally
    /// so a regression is visible to a reader, but it should never be non-zero.
    /// </summary>
    [Fact]
    public async Task The_churn_identity_holds_on_every_bucket()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddOutlet(VanAccount, "CORNER1", "Corner Store");

        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 6, 3));
        AddSale(Rep, "S2", "TUCK01", 45m, new DateTime(2026, 8, 4));
        AddSale(Rep, "S3", "CORNER1", 60m, new DateTime(2026, 8, 20));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.NotEmpty(report.Churn);
        Assert.All(report.Churn, bucket => Assert.Equal(0, bucket.UnexplainedMovement));
    }

    /// <summary>
    /// A shop that bought long ago and has bought again is returning, not new. Getting this wrong
    /// inflates acquisition by exactly the rate the base is churning, which flatters a route that is
    /// standing still.
    /// </summary>
    [Fact]
    public async Task A_shop_returning_after_a_long_silence_is_reactivated_not_new()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");

        // Two years earlier, then again inside the window.
        AddSale(Rep, "OLD", "TUCK01", 40m, new DateTime(2024, 8, 4));
        AddSale(Rep, "NEW", "TUCK01", 45m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var august = Assert.Single((await RunAsync()).Churn);

        Assert.Equal(0, august.NewOutlets);
        Assert.Equal(1, august.ReactivatedOutlets);
        Assert.Equal(1, august.BuyingOutlets);
    }

    /// <summary>A shop whose first ever purchase is inside the window is genuinely new.</summary>
    [Fact]
    public async Task A_shop_buying_for_the_first_time_is_new()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddSale(Rep, "FIRST", "TUCK01", 40m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var august = Assert.Single((await RunAsync()).Churn);

        Assert.Equal(1, august.NewOutlets);
        Assert.Equal(0, august.ReactivatedOutlets);
    }

    /// <summary>
    /// Lapsing is a transition, counted at the boundary it happened on. Counting it every bucket the
    /// shop stays quiet would make one lost customer look like a haemorrhage.
    /// </summary>
    [Fact]
    public async Task A_shop_lapses_once_not_every_month_it_stays_quiet()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");

        // Bought in March, silent since. With a 30-day lapse it crosses the line in April.
        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 3, 10));
        await _context.SaveChangesAsync();

        var report = await RunAsync(from: new DateTime(2026, 3, 1), to: new DateTime(2026, 7, 31), lapseDays: 30);

        Assert.Equal(1, report.Churn.Sum(bucket => bucket.LapsedOutlets));
        Assert.All(report.Churn, bucket => Assert.Equal(0, bucket.UnexplainedMovement));
    }

    // --- The outlet key ---

    /// <summary>
    /// The same code under two van accounts is two different shops. Grouping on the bare code would
    /// merge them, and the merged row would look entirely plausible.
    /// </summary>
    [Fact]
    public async Task The_same_outlet_code_under_two_accounts_is_two_shops()
    {
        AddOutlet(VanAccount, "SHOP1", "Shop One, Guruve");
        AddOutlet(OtherAccount, "SHOP1", "Shop One, Mutoko");

        AddSale(Rep, "S1", "SHOP1", 40m, new DateTime(2026, 8, 4), account: VanAccount);
        AddSale(OtherRep, "S2", "SHOP1", 60m, new DateTime(2026, 8, 5), account: OtherAccount);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Summary.OutletsBought);
        Assert.Equal(2, report.Outlets.Count);
        Assert.Equal(1, report.Quality.OutletCodesSharedAcrossAccounts);
    }

    // --- The uncovered register ---

    /// <summary>
    /// Three gaps, and they are different conversations: a shop nobody has ever called on, one that
    /// was missed this period, and one that was called on and bought nothing.
    /// </summary>
    [Fact]
    public async Task The_register_tells_the_three_kinds_of_gap_apart()
    {
        AddOutlet(VanAccount, "NEVER", "Never Visited");
        AddOutlet(VanAccount, "MISSED", "Missed This Month");
        AddOutlet(VanAccount, "BROWSED", "Called On, Bought Nothing");
        AddOutlet(VanAccount, "BOUGHT", "Bought");

        // Visited before the window, not in it.
        AddVisit(Rep, "MISSED", new DateTime(2026, 7, 10));

        AddVisit(Rep, "BROWSED", new DateTime(2026, 8, 6));
        AddVisit(Rep, "BOUGHT", new DateTime(2026, 8, 7));
        AddSale(Rep, "S1", "BOUGHT", 40m, new DateTime(2026, 8, 7));
        await _context.SaveChangesAsync();

        var register = (await RunAsync()).UncoveredOutlets;

        Assert.Equal(3, register.Count);
        Assert.Equal(VanSalesCoverageGap.NeverVisited,
            register.Single(row => row.OutletCode == "NEVER").Gap);
        Assert.Equal(VanSalesCoverageGap.NotVisitedInWindow,
            register.Single(row => row.OutletCode == "MISSED").Gap);
        Assert.Equal(VanSalesCoverageGap.VisitedNotBought,
            register.Single(row => row.OutletCode == "BROWSED").Gap);
        Assert.DoesNotContain(register, row => row.OutletCode == "BOUGHT");
    }

    /// <summary>
    /// A shop with no purchase anywhere in the read has never bought — a different finding from one
    /// that has lapsed, and the register must not blur them.
    /// </summary>
    [Fact]
    public async Task A_shop_that_never_bought_is_distinguished_from_one_that_lapsed()
    {
        AddOutlet(VanAccount, "NEW", "Never Converted");
        AddOutlet(VanAccount, "GONE", "Used To Buy");

        AddSale(Rep, "OLD", "GONE", 40m, new DateTime(2026, 6, 2));
        await _context.SaveChangesAsync();

        var register = (await RunAsync()).UncoveredOutlets;

        Assert.True(register.Single(row => row.OutletCode == "NEW").HasNeverBought);

        var lapsed = register.Single(row => row.OutletCode == "GONE");
        Assert.False(lapsed.HasNeverBought);
        Assert.Equal(new DateTime(2026, 6, 2), lapsed.LastPurchaseOn);
    }

    /// <summary>
    /// Ownership runs through the van's account, and several reps can share one, so it is a list.
    /// One name would be a guess.
    /// </summary>
    [Fact]
    public async Task An_uncovered_shop_names_every_rep_who_could_have_called()
    {
        AddUser(Guid.NewGuid(), "van010b", VanAccount);
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 8, 4));
        AddOutlet(VanAccount, "MISSED", "Missed");
        await _context.SaveChangesAsync();

        var missed = (await RunAsync()).UncoveredOutlets.Single(row => row.OutletCode == "MISSED");

        Assert.Contains("van010", missed.OwningReps);
    }

    // --- Location integrity ---

    /// <summary>
    /// A remembered fix is not a measured one, and no fix at all is not an imprecise one. All three
    /// are counted separately, because only the first says anything about where the rep was.
    /// </summary>
    [Fact]
    public async Task Fix_quality_is_counted_by_kind()
    {
        AddOutlet(VanAccount, "A", "A");
        AddVisit(Rep, "A", new DateTime(2026, 8, 4), source: TimesheetLocationSources.Gps, latitude: -17.8, accuracy: 12);
        AddVisit(Rep, "A", new DateTime(2026, 8, 5), source: TimesheetLocationSources.LastKnown, latitude: -17.8, accuracy: 40);
        AddVisit(Rep, "A", new DateTime(2026, 8, 6), source: TimesheetLocationSources.None, latitude: null, accuracy: null);
        AddVisit(Rep, "A", new DateTime(2026, 8, 7), source: TimesheetLocationSources.Gps, latitude: -17.8, accuracy: 900);
        await _context.SaveChangesAsync();

        var integrity = (await RunAsync()).LocationIntegrity;

        Assert.Equal(4, integrity.CallCount);
        Assert.Equal(2, integrity.CallsWithGpsFix);
        Assert.Equal(1, integrity.CallsWithLastKnownFix);
        Assert.Equal(1, integrity.CallsWithNoFix);
        Assert.Equal(1, integrity.CallsWithPoorAccuracy);
        Assert.Equal(0.5, integrity.GpsFixRate);
        Assert.False(integrity.IsClean);
    }

    /// <summary>
    /// An unstated accuracy is not an accurate fix. It is counted on its own and kept out of the
    /// poor-accuracy tally rather than being passed as good.
    /// </summary>
    [Fact]
    public async Task An_unstated_accuracy_is_neither_good_nor_poor()
    {
        AddOutlet(VanAccount, "A", "A");
        AddVisit(Rep, "A", new DateTime(2026, 8, 4), source: TimesheetLocationSources.Gps, latitude: -17.8, accuracy: null);
        await _context.SaveChangesAsync();

        var integrity = (await RunAsync()).LocationIntegrity;

        Assert.Equal(1, integrity.CallsWithoutAccuracy);
        Assert.Equal(0, integrity.CallsWithPoorAccuracy);
    }

    /// <summary>A period with no calls has no fix rate — not a perfect one.</summary>
    [Fact]
    public async Task A_period_with_no_calls_has_no_fix_rate()
    {
        AddOutlet(VanAccount, "A", "A");
        AddSale(Rep, "S1", "A", 40m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        Assert.Null((await RunAsync()).LocationIntegrity.GpsFixRate);
    }

    // --- Rates that must not become zeros ---

    /// <summary>
    /// A plan of zero is the handset's failed count, not a plan. Admitting it to the denominator
    /// turns an outage into non-compliance.
    /// </summary>
    [Fact]
    public async Task A_day_whose_plan_failed_is_excluded_from_call_compliance()
    {
        AddOutlet(VanAccount, "A", "A");
        AddRouteDay(Rep, new DateTime(2026, 8, 4), planned: 0);
        AddVisit(Rep, "A", new DateTime(2026, 8, 4));
        AddSale(Rep, "S1", "A", 40m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Null(report.Summary.PlannedCalls);
        Assert.Null(report.Summary.CallComplianceRate);
        Assert.Equal(1, report.Quality.DaysWithoutPlan);
    }

    /// <summary>A rep with sales and no calls recorded is unmeasurable, not a 0% striker.</summary>
    [Fact]
    public async Task A_rep_with_no_recorded_calls_has_no_strike_rate()
    {
        AddOutlet(VanAccount, "A", "A");
        AddSale(Rep, "S1", "A", 40m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var rep = Assert.Single((await RunAsync()).Reps);

        Assert.Null(rep.Calls);
        Assert.Null(rep.StrikeRate);
        Assert.Equal(1, rep.ProductiveCalls);
    }

    // --- Concentration ---

    /// <summary>
    /// Sales with no shop cannot be ranked, but their value has to be carried — otherwise a route
    /// whose attribution is mostly missing reads as one with a very short tail.
    /// </summary>
    [Fact]
    public async Task Unattributed_value_is_excluded_from_the_ranking_but_still_reported()
    {
        AddOutlet(VanAccount, "BIG", "Big Shop");
        AddSale(Rep, "S1", "BIG", 300m, new DateTime(2026, 8, 4));
        AddSale(Rep, "S2", null, 100m, new DateTime(2026, 8, 5));
        await _context.SaveChangesAsync();

        var row = Assert.Single((await RunAsync()).Concentration);

        Assert.Equal(1, row.OutletCount);
        Assert.Equal(300m, row.AttributedGross);
        Assert.Equal(100m, row.UnattributedGross);
        Assert.Equal(100d, row.Top1SharePercent);
        Assert.Equal(25d, row.UnattributedSharePercent);
    }

    /// <summary>The smallest number of shops carrying half the takings — the dependency figure.</summary>
    [Fact]
    public async Task Concentration_reports_how_few_shops_carry_half_the_takings()
    {
        for (var index = 0; index < 4; index++)
        {
            AddOutlet(VanAccount, $"S{index}", $"Shop {index}");
        }

        AddSale(Rep, "A", "S0", 500m, new DateTime(2026, 8, 4));
        AddSale(Rep, "B", "S1", 200m, new DateTime(2026, 8, 5));
        AddSale(Rep, "C", "S2", 200m, new DateTime(2026, 8, 6));
        AddSale(Rep, "D", "S3", 100m, new DateTime(2026, 8, 7));
        await _context.SaveChangesAsync();

        var row = Assert.Single((await RunAsync()).Concentration);

        Assert.Equal(1, row.OutletsForHalfOfGross);
        Assert.Equal(50d, row.Top1SharePercent);
    }

    // --- The lapsed register ---

    /// <summary>
    /// The last drop is the whole of that day's takings, not one document. A register showing the
    /// smaller of two invoices written at one counter understates what is being lost.
    /// </summary>
    [Fact]
    public async Task The_lapsed_register_values_the_whole_of_the_final_drop()
    {
        AddOutlet(VanAccount, "GONE", "Used To Buy");

        // Two invoices at one counter, and comfortably past the 90-day line rather than on it —
        // exactly 90 days is still active, and a test sitting on the boundary tests the boundary
        // rather than the register.
        AddSale(Rep, "S1", "GONE", 40m, new DateTime(2026, 5, 2));
        AddSale(Rep, "S2", "GONE", 60m, new DateTime(2026, 5, 2));
        await _context.SaveChangesAsync();

        var lapsed = Assert.Single((await RunAsync()).LapsedOutlets);

        Assert.Equal("GONE", lapsed.OutletCode);
        Assert.Equal(new DateTime(2026, 5, 2), lapsed.LastPurchaseOn);
        Assert.Equal(100m, Assert.Single(lapsed.LastPurchaseByCurrency).Gross);
        Assert.True(lapsed.StillOnRoster);
    }

    /// <summary>A shop still buying inside the window is not lapsed, however long ago it started.</summary>
    [Fact]
    public async Task A_shop_still_buying_is_not_on_the_lapsed_register()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddSale(Rep, "OLD", "TUCK01", 40m, new DateTime(2026, 5, 2));
        AddSale(Rep, "NEW", "TUCK01", 45m, new DateTime(2026, 8, 20));
        await _context.SaveChangesAsync();

        Assert.Empty((await RunAsync()).LapsedOutlets);
    }

    // --- Basket depth ---

    /// <summary>
    /// Frequency is distinct purchase days, so two invoices at one counter count once. Depth is
    /// distinct items in a drop, never a quantity — van lines carry no unit to sum.
    /// </summary>
    [Fact]
    public async Task Frequency_counts_days_and_depth_counts_items()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 8, 4), itemCode: "CHE011");
        AddSale(Rep, "S2", "TUCK01", 20m, new DateTime(2026, 8, 4), itemCode: "NRI049");
        AddSale(Rep, "S3", "TUCK01", 30m, new DateTime(2026, 8, 18), itemCode: "CHE011");
        await _context.SaveChangesAsync();

        var outlet = Assert.Single((await RunAsync()).Outlets);

        Assert.Equal(2, outlet.PurchaseDayCount);
        Assert.Equal(3, outlet.DocumentCount);
        Assert.Equal(2, outlet.DistinctItemCount);
        // Two items on the fourth, one on the eighteenth.
        Assert.Equal(1.5m, outlet.AverageItemsPerPurchase);
        Assert.Equal(14d, outlet.AverageDaysBetweenPurchases);
    }

    /// <summary>One purchase gives no interval — null, not zero.</summary>
    [Fact]
    public async Task A_single_purchase_has_no_interval_between_purchases()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        Assert.Null(Assert.Single((await RunAsync()).Outlets).AverageDaysBetweenPurchases);
    }

    // --- Validation ---

    [Fact]
    public async Task A_lapse_window_of_zero_days_is_refused()
    {
        var handler = new GetVanSalesCoverageReportHandler(_context);

        var result = await handler.Handle(
            new GetVanSalesCoverageReportQuery(From, To, LapseDays: 0),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesReports.InvalidLapseDays", result.FirstError.Code);
    }

    // --- Churn edges ---

    /// <summary>
    /// A period that opens mid-month still has to balance. The bucket is cut at the period's start,
    /// so a shop that bought before it is in the opening base rather than appearing from nowhere.
    /// </summary>
    [Fact]
    public async Task A_period_opening_mid_month_still_balances()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 8, 5));
        await _context.SaveChangesAsync();

        var report = await RunAsync(from: new DateTime(2026, 8, 15), to: new DateTime(2026, 9, 30));

        Assert.Equal(new DateTime(2026, 8, 15), report.Churn[0].BucketStart);
        Assert.Equal(1, report.Churn[0].OpeningActiveOutlets);
        Assert.All(report.Churn, bucket => Assert.Equal(0, bucket.UnexplainedMovement));
    }

    /// <summary>
    /// A lapse that falls after the period's last day has not happened yet as far as the report is
    /// concerned. The churn table and the win-back list are measured at the same date.
    /// </summary>
    [Fact]
    public async Task No_shop_lapses_after_the_period_ends()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");

        // Crosses a 90-day line on 8 Sep; the period ends on 1 Sep.
        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 6, 10));
        await _context.SaveChangesAsync();

        var report = await RunAsync(from: new DateTime(2026, 8, 1), to: new DateTime(2026, 9, 1));

        Assert.Equal(new DateTime(2026, 9, 1), report.Churn[^1].BucketEnd);
        Assert.Equal(0, report.Churn.Sum(bucket => bucket.LapsedOutlets));
        Assert.Empty(report.LapsedOutlets);
    }

    // --- The headline movements ---

    /// <summary>
    /// A shop that simply kept buying has not returned — it never left. The headline has to say the
    /// same as the monthly rows beneath it.
    /// </summary>
    [Fact]
    public async Task A_shop_that_kept_buying_is_not_counted_as_returned()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 7, 10));
        AddSale(Rep, "S2", "TUCK01", 40m, new DateTime(2026, 8, 10));
        await _context.SaveChangesAsync();

        var summary = (await RunAsync()).Summary;

        Assert.Equal(0, summary.ReactivatedOutlets);
        Assert.Equal(0, summary.NewOutlets);
        Assert.Equal(1, summary.OpeningActiveOutlets);
        Assert.Equal(1, summary.ClosingActiveOutlets);
    }

    /// <summary>
    /// Lapsed means gone past the chosen threshold. A shop that bought a week before the period and
    /// not during it is quiet, not lost.
    /// </summary>
    [Fact]
    public async Task The_headline_lapse_count_respects_the_lapse_threshold()
    {
        AddOutlet(VanAccount, "TUCK01", "Tuck Shop");
        AddSale(Rep, "S1", "TUCK01", 40m, new DateTime(2026, 7, 25));
        AddSale(Rep, "S2", "OTHER", 40m, new DateTime(2026, 8, 3));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(0, report.Summary.LapsedOutlets);
        Assert.Empty(report.LapsedOutlets);
    }

    /// <summary>
    /// The headline is the ledger's own totals: every movement sums its buckets, and opening plus
    /// the movements is closing.
    /// </summary>
    [Fact]
    public async Task The_headline_is_the_sum_of_the_ledger()
    {
        AddOutlet(VanAccount, "KEPT", "Kept");
        AddOutlet(VanAccount, "GONE", "Gone");
        AddOutlet(VanAccount, "BACK", "Back");
        AddOutlet(VanAccount, "FRESH", "Fresh");

        AddSale(Rep, "K1", "KEPT", 10m, new DateTime(2026, 5, 20));
        AddSale(Rep, "K2", "KEPT", 10m, new DateTime(2026, 7, 5));
        AddSale(Rep, "G1", "GONE", 10m, new DateTime(2026, 5, 1));
        AddSale(Rep, "B1", "BACK", 10m, new DateTime(2025, 11, 1));
        AddSale(Rep, "B2", "BACK", 10m, new DateTime(2026, 8, 12));
        AddSale(Rep, "F1", "FRESH", 10m, new DateTime(2026, 7, 20));
        await _context.SaveChangesAsync();

        var report = await RunAsync(from: new DateTime(2026, 7, 1), to: new DateTime(2026, 8, 31), lapseDays: 60);
        var summary = report.Summary;

        Assert.Equal(report.Churn.Sum(bucket => bucket.NewOutlets), summary.NewOutlets);
        Assert.Equal(report.Churn.Sum(bucket => bucket.ReactivatedOutlets), summary.ReactivatedOutlets);
        Assert.Equal(report.Churn.Sum(bucket => bucket.LapsedOutlets), summary.LapsedOutlets);
        Assert.Equal(report.Churn[0].OpeningActiveOutlets, summary.OpeningActiveOutlets);
        Assert.Equal(report.Churn[^1].ClosingActiveOutlets, summary.ClosingActiveOutlets);
        Assert.Equal(
            summary.ClosingActiveOutlets,
            summary.OpeningActiveOutlets + summary.NewOutlets + summary.ReactivatedOutlets - summary.LapsedOutlets);

        Assert.Equal(1, summary.NewOutlets);
        Assert.Equal(1, summary.ReactivatedOutlets);
        Assert.Equal(1, summary.LapsedOutlets);
    }

    // --- Reach is measured against the roster ---

    /// <summary>
    /// Coverage is a share of the roster, so only roster shops count towards it. A call on a shop
    /// that is not on the books is a call, but it reaches nothing the roster holds.
    /// </summary>
    [Fact]
    public async Task Calls_on_shops_off_the_roster_do_not_count_towards_coverage()
    {
        AddOutlet(VanAccount, "S1", "On the books");
        AddVisit(Rep, "S1", new DateTime(2026, 8, 4));
        AddVisit(Rep, "OFF1", new DateTime(2026, 8, 4), hour: 8);
        AddVisit(Rep, "OFF2", new DateTime(2026, 8, 4), hour: 9);
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Summary.OutletsVisited);
        Assert.Equal(1.0, report.Summary.RosterCoverageRate);
        Assert.Equal(1.0, Assert.Single(report.Reps).RosterCoverageRate);
    }

    /// <summary>
    /// A shop's code is unique only within its van's account. Another van calling on its own SHOP1
    /// has not called on this van's SHOP1.
    /// </summary>
    [Fact]
    public async Task A_call_on_another_vans_shop_with_the_same_code_reaches_only_that_shop()
    {
        AddOutlet(VanAccount, "SHOP1", "Ours");
        AddOutlet(OtherAccount, "SHOP1", "Theirs");
        AddSale(Rep, "S1", "ELSEWHERE", 10m, new DateTime(2026, 8, 4));
        AddVisit(OtherRep, "SHOP1", new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(1, report.Summary.OutletsVisited);
        Assert.Equal(VanSalesCoverageGap.NeverVisited,
            report.UncoveredOutlets.Single(row => row.VanAccountCode == VanAccount && row.OutletCode == "SHOP1").Gap);
        Assert.Equal(VanSalesCoverageGap.VisitedNotBought,
            report.UncoveredOutlets.Single(row => row.VanAccountCode == OtherAccount && row.OutletCode == "SHOP1").Gap);
    }

    // --- The not-bought register ---

    /// <summary>
    /// A shop that bought was reached, whether or not the handset recorded a check-in. It is not on
    /// the register, and the headline count is the register's own length.
    /// </summary>
    [Fact]
    public async Task A_shop_that_bought_without_a_check_in_is_not_on_the_register()
    {
        AddOutlet(VanAccount, "S1", "Bought");
        AddOutlet(VanAccount, "S2", "Missed");
        AddSale(Rep, "A", "S1", 10m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        var row = Assert.Single(report.UncoveredOutlets);
        Assert.Equal("S2", row.OutletCode);
        Assert.Equal(report.UncoveredOutlets.Count, report.Summary.OutletsUncovered);
    }

    /// <summary>
    /// "Never" means never, not "not in the last few months". A shop that was called on and bought
    /// in February has history, however short the lapse threshold is.
    /// </summary>
    [Fact]
    public async Task A_shop_with_old_history_is_not_read_as_never_called_or_never_bought()
    {
        AddOutlet(VanAccount, "S1", "Old Friend");
        AddVisit(Rep, "S1", new DateTime(2026, 2, 10));
        AddSale(Rep, "OLD", "S1", 10m, new DateTime(2026, 2, 10));
        AddSale(Rep, "X", "OTHER", 10m, new DateTime(2026, 8, 3));
        await _context.SaveChangesAsync();

        var row = (await RunAsync(lapseDays: 30)).UncoveredOutlets.Single(outlet => outlet.OutletCode == "S1");

        Assert.False(row.HasNeverBought);
        Assert.Equal(new DateTime(2026, 2, 10), row.LastPurchaseOn);
        Assert.Equal(new DateTime(2026, 2, 10), row.LastVisitedOn);
        Assert.Equal(VanSalesCoverageGap.NotVisitedInWindow, row.Gap);
    }

    /// <summary>
    /// A code typed in two cases is one shop. It must not bring the report down.
    /// </summary>
    [Fact]
    public async Task Check_ins_that_differ_only_in_case_are_one_shop()
    {
        AddOutlet(VanAccount, "ABC1", "Mixed Case");
        AddSale(Rep, "X", "OTHER", 10m, new DateTime(2026, 8, 3));
        AddVisit(Rep, "abc1", new DateTime(2026, 7, 10));
        AddVisit(Rep, "ABC1", new DateTime(2026, 7, 11));
        await _context.SaveChangesAsync();

        var row = (await RunAsync()).UncoveredOutlets.Single(outlet => outlet.OutletCode == "ABC1");

        Assert.Equal(new DateTime(2026, 7, 11), row.LastVisitedOn);
        Assert.Equal(VanSalesCoverageGap.NotVisitedInWindow, row.Gap);
    }

    // --- Calls against the plan ---

    /// <summary>
    /// A day whose plan failed is left out of both sides of the rate: its calls do not count against
    /// another day's plan.
    /// </summary>
    [Fact]
    public async Task Calls_on_a_day_without_a_plan_do_not_count_against_another_days_plan()
    {
        AddOutlet(VanAccount, "S1", "S1");
        AddRouteDay(Rep, new DateTime(2026, 8, 4), planned: 10);
        AddRouteDay(Rep, new DateTime(2026, 8, 5), planned: 0);
        AddVisit(Rep, "S1", new DateTime(2026, 8, 4));
        AddVisit(Rep, "S2", new DateTime(2026, 8, 4), hour: 8);
        for (var index = 0; index < 5; index++)
        {
            AddVisit(Rep, $"T{index}", new DateTime(2026, 8, 5), hour: 8 + index);
        }
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Summary.CallsAgainstPlan);
        Assert.Equal(0.2, report.Summary.CallComplianceRate!.Value, 3);
        Assert.Equal(0.2, report.Trend.Single().CallComplianceRate!.Value, 3);
        Assert.Equal(0.2, report.Reps.Single().CallComplianceRate!.Value, 3);
    }

    /// <summary>
    /// A van that went out with a plan and called on nobody missed the whole plan. The day counts,
    /// and the headline and the trend agree about it.
    /// </summary>
    [Fact]
    public async Task A_planned_day_with_no_calls_counts_as_missed()
    {
        AddOutlet(VanAccount, "S1", "S1");
        AddRouteDay(Rep, new DateTime(2026, 8, 4), planned: 10);
        AddRouteDay(Rep, new DateTime(2026, 8, 6), planned: 10);
        AddVisit(Rep, "S1", new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(20, report.Summary.PlannedCalls);
        Assert.Equal(20, report.Trend.Single().PlannedCalls);
        Assert.Equal(0.05, report.Summary.CallComplianceRate!.Value, 3);
        Assert.Equal(0.05, report.Trend.Single().CallComplianceRate!.Value, 3);
    }

    // --- Helpers ---

    private async Task<VanSalesCoverageReportResult> RunAsync(
        DateTime? from = null,
        DateTime? to = null,
        int lapseDays = 90,
        string? route = null)
    {
        var handler = new GetVanSalesCoverageReportHandler(_context);

        var result = await handler.Handle(
            new GetVanSalesCoverageReportQuery(from ?? From, to ?? To, RouteCode: route, LapseDays: lapseDays),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

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

    private void AddOutlet(string account, string code, string name) =>
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = account,
            Code = code,
            Name = name,
            IsActive = true,
            CreatedAt = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc)
        });

    private void AddRouteDay(Guid userId, DateTime tradingDate, int planned, string route = "GURUVE") =>
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = userId,
            Username = "van010",
            TradingDate = tradingDate,
            RouteCode = route,
            RouteName = route,
            Territory = "Mash Central",
            DepartedAt = tradingDate.AddHours(5),
            PlannedCustomerCount = planned
        });

    private void AddVisit(
        Guid userId,
        string customerCode,
        DateTime tradingDate,
        string source = TimesheetLocationSources.Gps,
        double? latitude = -17.8,
        double? accuracy = 15,
        int hour = 7) =>
        _context.TimesheetEntries.Add(new TimesheetEntryEntity
        {
            Channel = TimesheetChannel.VanSales,
            UserId = userId,
            Username = "van010",
            CustomerCode = customerCode,
            CustomerName = customerCode,
            // 09:00 CAT by default, comfortably inside the trading day either way.
            CheckInTime = tradingDate.AddHours(hour),
            CheckOutTime = tradingDate.AddHours(hour).AddMinutes(20),
            CheckInLatitude = latitude,
            CheckInLongitude = latitude is null ? null : 31.05,
            CheckInLocationSource = source,
            CheckInLocationAccuracyMetres = accuracy
        });

    private void AddSale(
        Guid userId,
        string reference,
        string? routeCustomerCode,
        decimal total,
        DateTime docDate,
        string account = VanAccount,
        string itemCode = "CHE011",
        string currency = "USD") =>
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
                    ItemCode = itemCode,
                    ItemDescription = $"Item {itemCode}",
                    Quantity = 1m,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = account
                }
            ]
        });
}
