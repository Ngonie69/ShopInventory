using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.VendingCashUps;
using ShopInventory.Features.VendingCashUps.Commands.ApproveVendingCashUp;
using ShopInventory.Features.VendingCashUps.Commands.QueryVendingCashUp;
using ShopInventory.Features.VendingCashUps.Commands.SaveVendingCashUp;
using ShopInventory.Features.VendingCashUps.Queries.GetVendingReconciliation;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// A vending cashier's cash-up: the variance arithmetic, and who may approve what.
/// </summary>
/// <remarks>
/// The rules the user set on 2026-11-13: one count per cashier, depot and day; a portal Cashier keys it
/// and an Admin approves it; nobody approves a count they keyed or edited; any variance needs a reason;
/// USD and ZiG are never added together. And from the reason the view was deferred in the first place:
/// a day with no count shows no variance.
/// </remarks>
public sealed class VendingCashUpTests : IDisposable
{
    private static readonly Guid Cashier = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherCashier = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Supervisor = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid AdminOne = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid AdminTwo = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid AdminThree = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Manager = Guid.Parse("40000000-0000-0000-0000-000000000001");

    private const string Depot = "DEPOT1";
    private static readonly DateTime Day = new(2026, 11, 10);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private int _saleNumber;

    public VendingCashUpTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            User(Cashier, "depot.till", ApplicationRoles.CartVendor, Depot),
            User(OtherCashier, "depot.till2", ApplicationRoles.CartVendor, Depot),
            User(Supervisor, "supervisor", ApplicationRoles.Cashier),
            User(AdminOne, "admin.one", ApplicationRoles.Admin),
            User(AdminTwo, "admin.two", ApplicationRoles.Admin),
            User(AdminThree, "admin.three", ApplicationRoles.Admin),
            User(Manager, "manager", ApplicationRoles.Manager));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── The arithmetic ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1050.00, 50.00, 1000.00, 0.00)]
    [InlineData(1005.50, 50.00, 962.80, -7.30)]
    [InlineData(1020.00, 20.00, 990.00, 10.00)]
    [InlineData(0.00, 0.00, 0.00, 0.00)]
    public void The_cash_variance_takes_the_float_out_of_the_count_before_comparing_it_with_the_sales(
        double counted, double openingFloat, double sales, double variance)
    {
        Assert.Equal((decimal)variance, VendingCashUpMath.CashVariance((decimal)counted, (decimal)openingFloat, (decimal)sales));
    }

    [Fact]
    public void A_sum_of_prices_a_hair_off_the_cent_is_not_a_variance()
    {
        // Three sales of 1/3 of a dollar each, as a till computing VAT on a price can produce.
        var sales = 0.3333333m + 0.3333333m + 0.3333334m;

        Assert.Equal(0m, VendingCashUpMath.CashVariance(1.00m, 0m, sales));
        Assert.True(VendingCashUpMath.IsBalanced([0.004m, -0.004m]));
        Assert.False(VendingCashUpMath.IsBalanced([0.005m]));
    }

    [Fact]
    public void The_notes_and_coins_add_up_to_the_count()
    {
        Assert.Equal(212.80m, VendingCashUpMath.DenominationTotal([(100m, 1), (50m, 2), (10m, 1), (0.5m, 5), (0.1m, 3)]));
    }

    [Theory]
    [InlineData("ZWG", "ZIG")]
    [InlineData("ZiG", "ZIG")]
    [InlineData(" zig ", "ZIG")]
    [InlineData("usd", "USD")]
    [InlineData(null, "USD")]
    [InlineData("", "USD")]
    public void ZiG_is_one_currency_however_the_till_spells_it(string? stored, string expected)
    {
        Assert.Equal(expected, VendingCashUpMath.NormalizeCurrency(stored));
    }

    [Fact]
    public void The_takings_to_bank_are_the_count_less_the_float()
    {
        Assert.Equal(162.80m, VendingCashUpMath.ToBank(212.80m, 50m));
        Assert.Equal(0m, VendingCashUpMath.ToBank(30m, 50m));
    }

    // ── Counting a day ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_drawer_that_holds_the_float_and_the_days_cash_balances_in_each_currency()
    {
        await AddSaleAsync(100.00m, "USD");
        await AddSaleAsync(62.80m, "USD", paymentMethod: "cash");
        await AddSaleAsync(300.00m, "ZWG");

        var result = await SaveAsync(Supervisor, 0,
            [Usd(openingFloat: 50m, notes: [(100m, 1), (50m, 2), (10m, 1), (0.5m, 5), (0.1m, 3)], banked: 162.80m),
             Zig(counted: 300m, banked: 300m)]);

        AssertOk(result);
        Assert.True(result.Value.Balanced);
        Assert.False(result.Value.NeedsReason);
        Assert.Equal(VendingCashUpStatuses.Submitted, result.Value.Status);

        var usd = result.Value.Currencies.Single(currency => currency.Currency == "USD");
        Assert.Equal(162.80m, usd.ExpectedCash);
        Assert.Equal(2, usd.ExpectedCashSales);
        Assert.Equal(212.80m, usd.CountedCash);
        Assert.Equal(0m, usd.Variance);
        Assert.Equal(162.80m, usd.ToBank);
        Assert.Equal(5, usd.Denominations.Count);

        var zig = result.Value.Currencies.Single(currency => currency.Currency == "ZIG");
        Assert.Equal(300m, zig.ExpectedCash);
        Assert.Equal(0m, zig.Variance);

        var saved = await _context.VendingCashUps.AsNoTracking().SingleAsync();
        Assert.True(saved.BalancedOnFirstCount);
        Assert.Equal(1, saved.Revision);
    }

    [Fact]
    public async Task A_vending_sale_with_no_tender_recorded_is_cash()
    {
        await AddSaleAsync(40m, "USD", paymentMethod: null);

        var result = await SaveAsync(Supervisor, 0, [Usd(counted: 40m)]);

        AssertOk(result);
        Assert.True(result.Value.Balanced);
        Assert.Empty(result.Value.Tenders);
    }

    [Fact]
    public async Task Only_this_cashiers_sales_at_this_warehouse_on_this_day_are_expected()
    {
        await AddSaleAsync(100m, "USD");
        await AddSaleAsync(55m, "USD", cashier: OtherCashier);
        await AddSaleAsync(66m, "USD", warehouse: "DEPOT2");
        await AddSaleAsync(77m, "USD", day: Day.AddDays(-1));
        await AddSaleAsync(88m, "USD", source: SaleSourceSystems.ShopTill);

        var result = await SaveAsync(Supervisor, 0, [Usd(counted: 100m)]);

        AssertOk(result);
        Assert.Equal(100m, result.Value.Currencies.Single(currency => currency.Currency == "USD").ExpectedCash);
        Assert.True(result.Value.Balanced);
    }

    [Fact]
    public async Task ZiG_sold_and_not_counted_is_a_ZiG_shortfall_and_never_offsets_USD_over()
    {
        await AddSaleAsync(100m, "USD");
        await AddSaleAsync(300m, "ZWG");

        // The supervisor keys USD only, and the USD is over by exactly what a careless sum would net off.
        var result = await SaveAsync(Supervisor, 0, [Usd(counted: 110m)]);

        AssertOk(result);
        Assert.False(result.Value.Balanced);
        Assert.Equal(10m, result.Value.Currencies.Single(currency => currency.Currency == "USD").Variance);
        Assert.Equal(-300m, result.Value.Currencies.Single(currency => currency.Currency == "ZIG").Variance);

        var lines = await _context.VendingCashUpCurrencies.AsNoTracking().ToListAsync();
        Assert.Contains(lines, line => line.Currency == "ZIG" && line.ExpectedCash == 300m && line.CountedCash == 0m);
    }

    [Fact]
    public async Task A_non_cash_tender_is_held_against_its_slips()
    {
        await AddSaleAsync(100m, "USD");
        await AddSaleAsync(25m, "USD", paymentMethod: "ecocash");

        var balanced = await SaveAsync(Supervisor, 0, [Usd(counted: 100m)], [new("USD", "EcoCash", 25m)]);
        AssertOk(balanced);
        Assert.True(balanced.Value.Balanced);
        Assert.Equal(0m, balanced.Value.Tenders.Single().Variance);

        var short_ = await SaveAsync(Supervisor, 1, [Usd(counted: 100m)], [new("USD", "Ecocash", 20m)]);
        AssertOk(short_);
        Assert.False(short_.Value.Balanced);
        Assert.Equal(-5m, short_.Value.Tenders.Single().Variance);
    }

    [Fact]
    public async Task Notes_that_disagree_with_the_typed_count_are_refused()
    {
        await AddSaleAsync(100m, "USD");

        var result = await SaveAsync(Supervisor, 0,
            [new SaveVendingCashUpCurrency("USD", 0m, 120m, 0m, null, [new(100m, 1)])]);

        Assert.True(result.IsError);
        Assert.Equal("VendingCashUp.DenominationsDisagree", result.FirstError.Code);
    }

    [Fact]
    public async Task Banking_more_than_was_counted_is_refused()
    {
        await AddSaleAsync(100m, "USD");

        var result = await SaveAsync(Supervisor, 0, [Usd(counted: 100m, banked: 150m)]);

        Assert.True(result.IsError);
        Assert.Equal("VendingCashUp.BankedMoreThanCounted", result.FirstError.Code);
    }

    [Fact]
    public async Task A_day_that_has_not_happened_cannot_be_counted()
    {
        var result = await SaveAsync(Supervisor, 0, [Usd(counted: 0m)], businessDate: VendingCashUpSheets.TodayCat().AddDays(1));

        Assert.True(result.IsError);
        Assert.Equal("VendingCashUp.DateInFuture", result.FirstError.Code);
    }

    [Fact]
    public async Task A_manager_cannot_key_a_count_and_nor_can_the_API_key_alone()
    {
        await AddSaleAsync(100m, "USD");

        var manager = await SaveAsync(Manager, 0, [Usd(counted: 100m)]);
        Assert.Equal("VendingCashUp.NotAllowedToCount", manager.FirstError.Code);

        var key = await SaveAsync(null, 0, [Usd(counted: 100m)]);
        Assert.Equal("VendingCashUp.SignedInUserRequired", key.FirstError.Code);
    }

    [Fact]
    public async Task A_second_count_replaces_the_first_only_against_the_revision_it_was_opened_on()
    {
        await AddSaleAsync(100m, "USD");
        AssertOk(await SaveAsync(Supervisor, 0, [Usd(counted: 90m)]));

        var stale = await SaveAsync(Supervisor, 0, [Usd(counted: 100m)]);
        Assert.Equal("VendingCashUp.Stale", stale.FirstError.Code);

        var recount = await SaveAsync(Supervisor, 1, [Usd(counted: 100m)]);
        AssertOk(recount);
        Assert.True(recount.Value.Balanced);
        Assert.Equal(2, recount.Value.Revision);

        var saved = await _context.VendingCashUps.AsNoTracking().SingleAsync();
        Assert.False(saved.BalancedOnFirstCount);
        Assert.Single(await _context.VendingCashUpCurrencies.AsNoTracking().Where(line => line.Currency == "USD").ToListAsync());
    }

    // ── Approving ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_balanced_count_is_approved_by_an_Admin_who_did_not_key_it()
    {
        await AddSaleAsync(100m, "USD");
        var saved = await SaveAsync(Supervisor, 0, [Usd(counted: 100m)]);

        var approved = await ApproveAsync(AdminOne, saved.Value.CashUpId!.Value, saved.Value.Revision);

        AssertOk(approved);
        Assert.Equal(VendingCashUpStatuses.Approved, approved.Value.Status);
        Assert.Equal("admin.one", approved.Value.ApprovedByName);
    }

    [Fact]
    public async Task Any_variance_needs_a_reason_before_it_is_approved()
    {
        await AddSaleAsync(100m, "USD");
        var saved = await SaveAsync(Supervisor, 0, [Usd(counted: 99.99m)]);
        Assert.True(saved.Value.NeedsReason);

        var refused = await ApproveAsync(AdminOne, saved.Value.CashUpId!.Value, saved.Value.Revision);
        Assert.Equal("VendingCashUp.ReasonRequired", refused.FirstError.Code);

        var explained = await SaveAsync(Supervisor, saved.Value.Revision, [Usd(counted: 99.99m)],
            varianceReason: "One cent short in change");
        Assert.False(explained.Value.NeedsReason);

        var approved = await ApproveAsync(AdminOne, explained.Value.CashUpId!.Value, explained.Value.Revision);
        AssertOk(approved);
    }

    [Fact]
    public async Task An_Admin_who_keyed_the_count_cannot_approve_it()
    {
        await AddSaleAsync(100m, "USD");
        var saved = await SaveAsync(AdminOne, 0, [Usd(counted: 100m)]);

        var own = await ApproveAsync(AdminOne, saved.Value.CashUpId!.Value, saved.Value.Revision);
        Assert.Equal("VendingCashUp.CannotApproveOwnCount", own.FirstError.Code);
        Assert.True(saved.Value.SavedByCaller);

        AssertOk(await ApproveAsync(AdminTwo, saved.Value.CashUpId!.Value, saved.Value.Revision));
    }

    [Fact]
    public async Task Everyone_who_edited_the_count_is_barred_from_approving_it_not_only_the_first_and_last()
    {
        await AddSaleAsync(100m, "USD");
        var first = await SaveAsync(AdminOne, 0, [Usd(counted: 90m)]);
        var second = await SaveAsync(AdminTwo, first.Value.Revision, [Usd(counted: 95m)]);
        var third = await SaveAsync(Supervisor, second.Value.Revision, [Usd(counted: 100m)]);
        var id = third.Value.CashUpId!.Value;

        Assert.Equal("VendingCashUp.CannotApproveOwnCount", (await ApproveAsync(AdminOne, id, third.Value.Revision)).FirstError.Code);
        Assert.Equal("VendingCashUp.CannotApproveOwnCount", (await ApproveAsync(AdminTwo, id, third.Value.Revision)).FirstError.Code);
        AssertOk(await ApproveAsync(AdminThree, id, third.Value.Revision));
    }

    [Fact]
    public async Task Only_an_Admin_approves_or_queries()
    {
        await AddSaleAsync(100m, "USD");
        var saved = await SaveAsync(AdminOne, 0, [Usd(counted: 100m)]);
        var id = saved.Value.CashUpId!.Value;

        Assert.Equal("VendingCashUp.ApproverMustBeAdmin", (await ApproveAsync(Supervisor, id, saved.Value.Revision)).FirstError.Code);
        Assert.Equal("VendingCashUp.ApproverMustBeAdmin", (await ApproveAsync(Manager, id, saved.Value.Revision)).FirstError.Code);
        Assert.Equal("VendingCashUp.ApproverMustBeAdmin", (await QueryAsync(Supervisor, id, saved.Value.Revision, "Why?")).FirstError.Code);
        Assert.Equal("VendingCashUp.SignedInUserRequired", (await ApproveAsync(null, id, saved.Value.Revision)).FirstError.Code);
    }

    [Fact]
    public async Task A_sale_that_lands_after_the_count_stops_the_approval_until_it_is_counted_again()
    {
        await AddSaleAsync(100m, "USD");
        var saved = await SaveAsync(Supervisor, 0, [Usd(counted: 120m)], varianceReason: "Over by a sale not yet uploaded");

        // The till was offline and its last sale arrives after the supervisor counted.
        await AddSaleAsync(20m, "USD");

        var refused = await ApproveAsync(AdminOne, saved.Value.CashUpId!.Value, saved.Value.Revision);
        Assert.Equal("VendingCashUp.SalesChangedSinceCount", refused.FirstError.Code);

        var sheet = await SheetAsync(Supervisor);
        Assert.True(sheet.SalesChangedSinceCount);
        Assert.Equal(0m, sheet.Currencies.Single(currency => currency.Currency == "USD").Variance);

        var recount = await SaveAsync(Supervisor, saved.Value.Revision, [Usd(counted: 120m)]);
        Assert.True(recount.Value.Balanced);
        Assert.False(recount.Value.SalesChangedSinceCount);
        AssertOk(await ApproveAsync(AdminOne, recount.Value.CashUpId!.Value, recount.Value.Revision));
    }

    [Fact]
    public async Task An_approved_count_can_no_longer_change()
    {
        await AddSaleAsync(100m, "USD");
        var saved = await SaveAsync(Supervisor, 0, [Usd(counted: 100m)]);
        var approved = await ApproveAsync(AdminOne, saved.Value.CashUpId!.Value, saved.Value.Revision);

        var recount = await SaveAsync(Supervisor, approved.Value.Revision, [Usd(counted: 90m)]);
        Assert.Equal("VendingCashUp.AlreadyApproved", recount.FirstError.Code);

        var again = await ApproveAsync(AdminTwo, approved.Value.CashUpId!.Value, approved.Value.Revision);
        Assert.Equal("VendingCashUp.AlreadyApproved", again.FirstError.Code);
    }

    [Fact]
    public async Task A_queried_count_waits_for_an_answer_before_it_can_be_approved()
    {
        await AddSaleAsync(100m, "USD");
        var saved = await SaveAsync(Supervisor, 0, [Usd(counted: 88m)], varianceReason: "Unknown");
        var id = saved.Value.CashUpId!.Value;

        Assert.Equal("VendingCashUp.QueryNoteRequired", (await QueryAsync(AdminOne, id, saved.Value.Revision, "  ")).FirstError.Code);

        var queried = await QueryAsync(AdminOne, id, saved.Value.Revision, "Recount the drawer, 12 is a lot");
        AssertOk(queried);
        Assert.Equal(VendingCashUpStatuses.Queried, queried.Value.Status);
        Assert.Equal("Recount the drawer, 12 is a lot", queried.Value.QueryNote);

        var early = await ApproveAsync(AdminOne, id, queried.Value.Revision);
        Assert.Equal("VendingCashUp.NotAwaitingApproval", early.FirstError.Code);

        var answered = await SaveAsync(Supervisor, queried.Value.Revision, [Usd(counted: 100m)]);
        Assert.Equal(VendingCashUpStatuses.Submitted, answered.Value.Status);
        AssertOk(await ApproveAsync(AdminOne, id, answered.Value.Revision));
    }

    [Fact]
    public async Task A_decision_against_an_old_revision_is_refused()
    {
        await AddSaleAsync(100m, "USD");
        var first = await SaveAsync(Supervisor, 0, [Usd(counted: 100m)]);
        await SaveAsync(Supervisor, first.Value.Revision, [Usd(counted: 100m)]);

        var stale = await ApproveAsync(AdminOne, first.Value.CashUpId!.Value, first.Value.Revision);
        Assert.Equal("VendingCashUp.Stale", stale.FirstError.Code);
    }

    // ── The reconciliation ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_day_with_sales_and_no_count_is_not_cashed_up_and_shows_no_variance()
    {
        await AddSaleAsync(250m, "USD");

        var report = await ReconcileAsync();

        var row = Assert.Single(report.Rows);
        Assert.Equal(VendingCashUpReconciliationStatus.NotCashedUp, row.Status);
        Assert.Null(row.CashUpId);
        var usd = Assert.Single(row.Currencies);
        Assert.Equal(250m, usd.Invoiced);
        Assert.Null(usd.CountedCash);
        Assert.Null(usd.Accounted);
        Assert.Null(usd.Variance);
        Assert.False(row.NeedsReason);

        var total = Assert.Single(report.Totals);
        Assert.Equal(250m, total.Invoiced);
        Assert.Equal(0m, total.InvoicedOnCountedDays);
        Assert.Equal(0m, total.NetVariance);
        Assert.Equal(0m, total.OpenShortfall);
        Assert.Equal(1, report.Counts.NotCashedUp);
    }

    [Fact]
    public async Task The_reconciliation_counts_shortfalls_still_open_and_not_those_approved()
    {
        await AddSaleAsync(100m, "USD");
        await AddSaleAsync(80m, "USD", cashier: OtherCashier);
        await AddSaleAsync(60m, "USD", day: Day.AddDays(-1));

        var shortToday = await SaveAsync(Supervisor, 0, [Usd(counted: 95m)], varianceReason: "Change given twice");
        await SaveAsync(Supervisor, 0, [Usd(openingFloat: 20m, counted: 100m)], cashier: OtherCashier);
        var shortYesterday = await SaveAsync(Supervisor, 0, [Usd(counted: 57m)], varianceReason: "Note torn, rejected by bank",
            businessDate: Day.AddDays(-1));
        AssertOk(await ApproveAsync(AdminOne, shortYesterday.Value.CashUpId!.Value, shortYesterday.Value.Revision));

        var report = await ReconcileAsync(from: Day.AddDays(-1));

        Assert.Equal(3, report.Rows.Count);
        Assert.Equal(Day, report.Rows[0].BusinessDate);

        var today = report.Rows.Single(row => row.CashUpId == shortToday.Value.CashUpId);
        var usd = today.Currencies.Single();
        Assert.Equal(100m, usd.Invoiced);
        Assert.Equal(95m, usd.Accounted);
        Assert.Equal(-5m, usd.Variance);

        var other = report.Rows.Single(row => row.CashierUserId == OtherCashier);
        Assert.Equal(80m, other.Currencies.Single().Accounted);
        Assert.True(other.Balanced);

        var total = Assert.Single(report.Totals);
        Assert.Equal(240m, total.Invoiced);
        Assert.Equal(240m, total.InvoicedOnCountedDays);
        Assert.Equal(-8m, total.NetVariance);
        Assert.Equal(5m, total.OpenShortfall);
        Assert.Equal(1, report.Counts.Approved);
        Assert.Equal(2, report.Counts.AwaitingApproval);
        Assert.Equal(1, report.Counts.BalancedOnFirstCount);
    }

    [Fact]
    public async Task A_count_of_a_cashier_who_sold_nothing_is_still_on_the_reconciliation()
    {
        var saved = await SaveAsync(Supervisor, 0, [Usd(openingFloat: 50m, counted: 50m)]);
        AssertOk(saved);
        Assert.True(saved.Value.Balanced);

        var row = Assert.Single((await ReconcileAsync()).Rows);
        Assert.Equal(VendingCashUpStatuses.Submitted, row.Status);
        Assert.Equal(0m, Assert.Single(row.Currencies).Variance);
    }

    [Fact]
    public void The_validator_refuses_cash_as_a_declared_tender_and_a_currency_counted_twice()
    {
        var validator = new SaveVendingCashUpValidator();

        var cashTender = validator.Validate(Command(Supervisor, 0, [Usd(counted: 1m)], [new("USD", "Cash", 1m)]));
        Assert.False(cashTender.IsValid);

        var twice = validator.Validate(Command(Supervisor, 0, [Usd(counted: 1m), new SaveVendingCashUpCurrency("usd", 0m, 1m, 0m, null, null)], []));
        Assert.False(twice.IsValid);

        var zigTwice = validator.Validate(Command(Supervisor, 0, [Zig(counted: 1m), new SaveVendingCashUpCurrency("ZWG", 0m, 1m, 0m, null, null)], []));
        Assert.False(zigTwice.IsValid);

        Assert.True(validator.Validate(Command(Supervisor, 0, [Usd(counted: 1m), Zig(counted: 1m)], [new("USD", "Swipe", 1m)])).IsValid);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static User User(Guid id, string username, string role, string? warehouse = null)
    {
        var user = new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = role,
            IsActive = true,
            AssignedBusinessPartnerCode = role == ApplicationRoles.CartVendor ? "DEPOTBP" : null
        };

        if (warehouse is not null)
        {
            user.SetWarehouseCodes([warehouse]);
        }

        return user;
    }

    private static SaveVendingCashUpCurrency Usd(
        decimal openingFloat = 0m,
        decimal? counted = null,
        decimal banked = 0m,
        (decimal Value, int Quantity)[]? notes = null) =>
        new("USD", openingFloat, counted, banked, null, notes?.Select(note => new SaveVendingCashUpDenomination(note.Value, note.Quantity)).ToList());

    private static SaveVendingCashUpCurrency Zig(decimal counted, decimal banked = 0m) =>
        new("ZIG", 0m, counted, banked, null, null);

    private async Task AddSaleAsync(
        decimal total,
        string currency,
        string? paymentMethod = TenderTypes.Cash,
        Guid? cashier = null,
        string warehouse = Depot,
        DateTime? day = null,
        string source = SaleSourceSystems.Vending)
    {
        _saleNumber++;
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"VEND-{_saleNumber}",
            SourceSystem = source,
            CardCode = "DEPOTBP",
            CardName = "Test depot",
            DocDate = (day ?? Day).Date,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            WarehouseCode = warehouse,
            PaymentMethod = paymentMethod,
            AmountPaid = total,
            CreatedBy = (cashier ?? Cashier).ToString(),
            CreatedAt = (day ?? Day).Date.AddHours(9)
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static SaveVendingCashUpCommand Command(
        Guid? actor,
        int revision,
        IReadOnlyList<SaveVendingCashUpCurrency> currencies,
        IReadOnlyList<SaveVendingCashUpTender>? tenders = null,
        string? varianceReason = null,
        Guid? cashier = null,
        DateTime? businessDate = null) =>
        new(cashier ?? Cashier, Depot, businessDate ?? Day, revision, currencies, tenders ?? [], varianceReason, null, actor);

    private async Task<ErrorOr<VendingCashUpSheetDto>> SaveAsync(
        Guid? actor,
        int revision,
        IReadOnlyList<SaveVendingCashUpCurrency> currencies,
        IReadOnlyList<SaveVendingCashUpTender>? tenders = null,
        string? varianceReason = null,
        Guid? cashier = null,
        DateTime? businessDate = null)
    {
        var result = await new SaveVendingCashUpHandler(_context, new NoOpAuditService(), NullLogger<SaveVendingCashUpHandler>.Instance)
            .Handle(Command(actor, revision, currencies, tenders, varianceReason, cashier, businessDate), CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr<VendingCashUpSheetDto>> ApproveAsync(Guid? actor, int cashUpId, int revision)
    {
        var result = await new ApproveVendingCashUpHandler(_context, new NoOpAuditService(), NullLogger<ApproveVendingCashUpHandler>.Instance)
            .Handle(new ApproveVendingCashUpCommand(cashUpId, revision, actor), CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ErrorOr<VendingCashUpSheetDto>> QueryAsync(Guid? actor, int cashUpId, int revision, string? note)
    {
        var result = await new QueryVendingCashUpHandler(_context, new NoOpAuditService(), NullLogger<QueryVendingCashUpHandler>.Instance)
            .Handle(new QueryVendingCashUpCommand(cashUpId, revision, note, actor), CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<VendingCashUpSheetDto> SheetAsync(Guid caller)
    {
        var cashUp = await VendingCashUpSheets.FindAsync(_context, Cashier, Depot, Day, tracking: false, CancellationToken.None);
        return await VendingCashUpSheets.BuildAsync(_context, Cashier, Depot, Day, cashUp, caller, CancellationToken.None);
    }

    private async Task<VendingReconciliationDto> ReconcileAsync(DateTime? from = null)
    {
        var result = await new GetVendingReconciliationHandler(_context)
            .Handle(new GetVendingReconciliationQuery(from ?? Day, Day, null), CancellationToken.None);
        AssertOk(result);
        return result.Value;
    }

    private static void AssertOk<T>(ErrorOr<T> result) =>
        Assert.False(result.IsError, result.IsError ? $"{result.FirstError.Code}: {result.FirstError.Description}" : null);

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }
}
