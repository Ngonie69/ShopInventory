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

    /// <summary>
    /// The defect this pins: the coverage numerator counted every call, while the denominator counted
    /// only the active roster.
    ///
    /// A check-in validates its customer code against nothing, so the visit set holds shops
    /// deactivated since and shops on nobody's roster. Dividing that by an active-only roster printed
    /// coverage above 100% on the same page as a non-empty uncovered register.
    /// </summary>
    [Fact]
    public async Task Coverage_never_exceeds_the_roster_it_is_divided_by()
    {
        AddOutlet(VanAccount, "SHOP1", "On The Books");
        AddOutlet(VanAccount, "SHOP2", "Also On The Books");

        AddVisit(Rep, "SHOP1", new DateTime(2026, 8, 4));
        AddVisit(Rep, "SHOP2", new DateTime(2026, 8, 5));
        // Three shops this rep called at that no roster holds — deactivated since, or never on one.
        AddVisit(Rep, "GONE1", new DateTime(2026, 8, 6));
        AddVisit(Rep, "GONE2", new DateTime(2026, 8, 7));
        AddVisit(Rep, "NEVER-ON-A-ROSTER", new DateTime(2026, 8, 8));
        AddSale(Rep, "S1", "SHOP1", 40m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(2, report.Summary.RosterSize);
        Assert.Equal(5, report.Summary.OutletsVisited);
        Assert.Equal(2, report.Summary.OutletsVisitedOnRoster);
        Assert.Equal(3, report.Summary.OutletsVisitedOffRoster);

        // The whole point: it lands on 100%, not 250%.
        Assert.Equal(1.0, report.Summary.RosterCoverageRate);

        var rep = Assert.Single(report.Reps);
        Assert.Equal(2, rep.OutletsVisitedOnRoster);
        Assert.Equal(3, rep.OutletsVisitedOffRoster);
        Assert.Equal(1.0, rep.RosterCoverageRate);
    }

    /// <summary>
    /// The rate and the uncovered register have to agree. Full coverage means an empty register, and
    /// a non-empty register means the rate is below 100% — they used to be able to contradict each
    /// other on one page.
    /// </summary>
    [Fact]
    public async Task The_coverage_rate_and_the_uncovered_register_cannot_contradict_each_other()
    {
        AddOutlet(VanAccount, "REACHED", "Reached");
        AddOutlet(VanAccount, "MISSED", "Missed");

        AddVisit(Rep, "REACHED", new DateTime(2026, 8, 4));
        AddVisit(Rep, "OFF-ROSTER", new DateTime(2026, 8, 5));
        AddSale(Rep, "S1", "REACHED", 40m, new DateTime(2026, 8, 4));
        await _context.SaveChangesAsync();

        var report = await RunAsync();

        Assert.Equal(0.5, report.Summary.RosterCoverageRate);
        Assert.True(report.Summary.RosterCoverageRate < 1.0);
        Assert.NotEmpty(report.UncoveredOutlets);
        Assert.Contains(report.UncoveredOutlets, row => row.OutletCode == "MISSED");
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

    // --- Helpers ---

    private async Task<VanSalesCoverageReportResult> RunAsync(
        DateTime? from = null,
        DateTime? to = null,
        int lapseDays = 90)
    {
        var handler = new GetVanSalesCoverageReportHandler(_context);

        var result = await handler.Handle(
            new GetVanSalesCoverageReportQuery(from ?? From, to ?? To, LapseDays: lapseDays),
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

    private void AddVisit(
        Guid userId,
        string customerCode,
        DateTime tradingDate,
        string source = TimesheetLocationSources.Gps,
        double? latitude = -17.8,
        double? accuracy = 15) =>
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
        string itemCode = "CHE011") =>
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
            Currency = "USD",
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
