using ShopInventory.Web.Models;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The compliance rulebook, pinned.
/// </summary>
/// <remarks>
/// These eight rules decided who passed for as long as they lived inside a <c>@code</c> block,
/// where no test could reach them. They are about to be changed — the late-departure rule moves
/// from the rep's handset to the vehicle's ignition — so they are pinned first, unchanged, and
/// the change is made against a net rather than against a 1,258-line file.
///
/// Written against behaviour, not implementation: each fact says what a supervisor would see.
/// </remarks>
public class DepartureComplianceClassifierTests
{
    private static DateTime Day(int hour, int minute) =>
        new(2026, 9, 18, hour, minute, 0, DateTimeKind.Unspecified);

    /// <summary>
    /// A clean day: departed before seven, both odometer readings taken, every planned call made
    /// and productive, and the takings counted back exactly.
    /// </summary>
    private static DepartureComplianceDay Clean() => new()
    {
        HasDayRecord = true,
        TimeOut = Day(6, 40),
        TimeIn = Day(16, 10),
        IsClosed = true,
        StartingMileage = 41_000,
        ClosingMileage = 41_210,
        PlannedCustomerCount = 10,
        CustomersVisited = 10,
        ProductiveCalls = 10,
        SystemCash = 500m,
        SystemTotalSales = 500m,
        DeclaredCash = 500m
    };

    [Fact]
    public void A_clean_day_carries_no_gap_and_no_badge()
    {
        var day = Clean();

        Assert.Equal(Gaps.None, DepartureComplianceClassifier.GapsOf(day));
        Assert.Null(DepartureComplianceClassifier.PrimaryFlag(Gaps.None));
        Assert.Equal(string.Empty, DepartureComplianceClassifier.FlagList(Gaps.None));
    }

    // — Departure ————————————————————————————————————————————————————

    [Fact]
    public void Leaving_after_seven_is_late()
    {
        var day = Clean();
        day.TimeOut = Day(7, 1);

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.LateOut));
    }

    [Fact]
    public void Leaving_at_exactly_seven_is_not_late()
    {
        // The rule is a strict comparison and there is no grace period. Both halves of that are
        // policy, so both are pinned: seven o'clock exactly passes, and 07:01 does not.
        var day = Clean();
        day.TimeOut = Day(7, 0);

        Assert.False(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.LateOut));
    }

    [Fact]
    public void A_day_with_no_start_record_is_one_finding_not_three()
    {
        // No Start Day means no departure time and no opening mileage either. Marking the day
        // late and odometer-less as well would put three marks on one omission.
        var day = new DepartureComplianceDay { HasDayRecord = false };

        var gaps = DepartureComplianceClassifier.GapsOf(day);

        Assert.True(gaps.HasFlag(Gaps.NoDeparture));
        Assert.False(gaps.HasFlag(Gaps.LateOut));
        Assert.False(gaps.HasFlag(Gaps.NoOdometer));
    }

    // — Odometer ——————————————————————————————————————————————————————

    [Fact]
    public void A_missing_closing_reading_is_no_odometer()
    {
        var day = Clean();
        day.ClosingMileage = null;

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.NoOdometer));
    }

    [Fact]
    public void A_closing_reading_below_the_opening_one_is_no_odometer()
    {
        // The distance is null rather than negative, so this lands as an unusable reading rather
        // than as a van that drove backwards.
        var day = Clean();
        day.ClosingMileage = 40_900;

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.NoOdometer));
    }

    // — Money ————————————————————————————————————————————————————————

    [Fact]
    public void Selling_and_declaring_nothing_is_a_finding()
    {
        var day = Clean();
        day.DeclaredCash = null;

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.NothingDeclared));
    }

    [Fact]
    public void A_day_with_no_sales_and_no_declaration_is_clean()
    {
        // A rep who was on the road and sold nothing has nothing to declare. Flagging that would
        // mark the one row where the takings and the declaration already agree.
        var day = Clean();
        day.SystemCash = 0m;
        day.SystemTotalSales = 0m;
        day.DeclaredCash = null;

        Assert.Equal(Gaps.None, DepartureComplianceClassifier.GapsOf(day));
    }

    [Fact]
    public void Counting_back_less_than_the_declarable_takings_is_short()
    {
        var day = Clean();
        day.DeclaredCash = 450m;

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.ShortDeclaration));
    }

    [Fact]
    public void A_swipe_the_handset_gave_no_box_for_does_not_make_the_rep_short()
    {
        // The shortfall is measured against the takings the rep could declare, not the day's
        // sales: an untendered sale has no box on the handset and used to mark the rep short by
        // exactly its value.
        var day = Clean();
        day.SystemUntendered = 200m;
        day.SystemTotalSales = 700m;

        Assert.False(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.ShortDeclaration));
    }

    [Fact]
    public void Counting_back_more_than_the_day_can_account_for_is_an_overage()
    {
        var day = Clean();
        day.DeclaredCash = 560m;

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.OverDeclaration));
    }

    [Fact]
    public void An_overage_within_the_untendered_sales_is_allowed()
    {
        // Every untendered sale is allowed to have been cash the rep collected before anything is
        // called unaccounted for.
        var day = Clean();
        day.SystemUntendered = 100m;
        day.DeclaredCash = 560m;

        Assert.False(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.OverDeclaration));
    }

    // — Rates ————————————————————————————————————————————————————————

    [Fact]
    public void A_call_rate_under_ninety_five_percent_is_under_target()
    {
        var day = Clean();
        day.CustomersVisited = 9;

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.CcrUnderTarget));
    }

    [Fact]
    public void A_productive_rate_under_seventy_five_percent_is_under_target()
    {
        var day = Clean();
        day.ProductiveCalls = 7;

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.PcrUnderTarget));
    }

    [Fact]
    public void A_day_with_nothing_planned_has_no_rate_to_be_under()
    {
        // Zero planned calls gives a null rate, not a zero one, so the day is not marked for
        // missing a target that was never set.
        var day = Clean();
        day.PlannedCustomerCount = 0;
        day.CustomersVisited = 0;
        day.ProductiveCalls = 0;

        var gaps = DepartureComplianceClassifier.GapsOf(day);

        Assert.False(gaps.HasFlag(Gaps.CcrUnderTarget));
        Assert.False(gaps.HasFlag(Gaps.PcrUnderTarget));
    }

    // — The badge ——————————————————————————————————————————————————————

    [Theory]
    [InlineData(Gaps.NoDeparture, "No departure record", true)]
    [InlineData(Gaps.NothingDeclared, "Nothing declared", true)]
    [InlineData(Gaps.ShortDeclaration, "Short declaration", true)]
    [InlineData(Gaps.OverDeclaration, "Over declaration", true)]
    [InlineData(Gaps.LateOut, "Late out", false)]
    [InlineData(Gaps.NoOdometer, "No odometer", false)]
    public void Each_gap_that_carries_a_badge_carries_its_own(Gaps gap, string label, bool strong)
    {
        var flag = DepartureComplianceClassifier.PrimaryFlag(gap);

        Assert.NotNull(flag);
        Assert.Equal(label, flag!.Value.Label);
        Assert.Equal(strong, flag.Value.Strong);
    }

    [Fact]
    public void A_rate_under_target_never_becomes_the_badge()
    {
        // The figure is already marked in its own column, next to the numbers that explain it.
        Assert.Null(DepartureComplianceClassifier.PrimaryFlag(Gaps.CcrUnderTarget));
        Assert.Null(DepartureComplianceClassifier.PrimaryFlag(Gaps.PcrUnderTarget));
    }

    [Fact]
    public void The_badge_follows_a_stated_order_of_precedence()
    {
        var everything = Gaps.NoDeparture | Gaps.NothingDeclared | Gaps.ShortDeclaration
                         | Gaps.OverDeclaration | Gaps.LateOut | Gaps.NoOdometer;

        Assert.Equal("No departure record", DepartureComplianceClassifier.PrimaryFlag(everything)!.Value.Label);
        Assert.Equal("Nothing declared",
            DepartureComplianceClassifier.PrimaryFlag(everything & ~Gaps.NoDeparture)!.Value.Label);
        Assert.Equal("Late out",
            DepartureComplianceClassifier.PrimaryFlag(Gaps.LateOut | Gaps.NoOdometer)!.Value.Label);
    }

    // — The chips ——————————————————————————————————————————————————————

    [Fact]
    public void The_first_chip_keeps_every_row()
    {
        var all = DepartureComplianceClassifier.Filters[0];

        Assert.Equal(DepartureComplianceClassifier.AllRepDays, all.Key);
        Assert.True(all.Test(Gaps.None));
        Assert.True(all.Test(Gaps.NoDeparture));
    }

    [Fact]
    public void The_money_chip_gathers_all_three_money_findings()
    {
        var money = DepartureComplianceClassifier.Filters.Single(filter => filter.Key == "money");

        Assert.True(money.Test(Gaps.NothingDeclared));
        Assert.True(money.Test(Gaps.ShortDeclaration));
        Assert.True(money.Test(Gaps.OverDeclaration));
        Assert.False(money.Test(Gaps.LateOut));
    }

    [Fact]
    public void Every_chip_has_a_distinct_key()
    {
        var keys = DepartureComplianceClassifier.Filters.Select(filter => filter.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    // — The export ——————————————————————————————————————————————————————

    [Fact]
    public void The_export_lists_every_finding_on_the_row()
    {
        var list = DepartureComplianceClassifier.FlagList(
            Gaps.LateOut | Gaps.NoOdometer | Gaps.CcrUnderTarget);

        Assert.Equal("Late out; No odometer; CCR under target", list);
    }
}
