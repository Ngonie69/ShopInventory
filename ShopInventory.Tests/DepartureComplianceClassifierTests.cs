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

    // — The verdict moves to the vehicle ——————————————————————————————

    /// <summary>A vehicle that left at <paramref name="hour"/>:<paramref name="minute"/>.</summary>
    private static DepartureComplianceTelematicsDay Vehicle(
        int hour, int minute, int? distanceKm = null) => new()
        {
            Registration = "AFQ9644",
            Match = TelematicsMatch.Matched,
            HasRollup = true,
            MovementRead = true,
            OdometerRead = true,
            FirstIgnitionOn = Day(hour, minute).AddMinutes(-40),
            FirstDeparture = Day(hour, minute),
            DistanceKm = distanceKm
        };

    [Fact]
    public void The_vehicle_decides_when_it_has_an_answer()
    {
        // The whole point. The rep taps Start Day at 06:55 and the van leaves at 07:40; the
        // handset alone recorded that as on time and nothing disagreed.
        var day = Clean();
        day.TimeOut = Day(6, 55);
        day.Telematics = Vehicle(7, 40);

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.LateOut));
    }

    [Fact]
    public void The_vehicle_can_clear_a_rep_the_handset_would_have_marked()
    {
        // It cuts both ways, which is the reason it is fair to use it.
        var day = Clean();
        day.TimeOut = Day(7, 20);
        day.Telematics = Vehicle(6, 45);

        Assert.False(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.LateOut));
    }

    [Fact]
    public void The_handset_still_decides_when_the_vehicle_has_no_answer()
    {
        var day = Clean();
        day.TimeOut = Day(7, 30);
        day.Telematics = new DepartureComplianceTelematicsDay
        {
            Match = TelematicsMatch.NoRegistration
        };

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.LateOut));
    }

    [Fact]
    public void A_vehicle_that_only_started_falls_back_to_its_ignition()
    {
        // No departure but an ignition: the van started and never left, so the ignition is the
        // only thing the vehicle can say about when the day began.
        var day = Clean();
        day.TimeOut = Day(6, 30);
        day.Telematics = new DepartureComplianceTelematicsDay
        {
            Match = TelematicsMatch.Matched,
            HasRollup = true,
            MovementRead = true,
            FirstIgnitionOn = Day(7, 45)
        };

        Assert.True(DepartureComplianceClassifier.GapsOf(day).HasFlag(Gaps.LateOut));
    }

    // — Signals are never findings ————————————————————————————————————

    [Fact]
    public void A_disagreement_is_shown_and_never_scored()
    {
        var day = Clean();
        day.TimeOut = Day(6, 10);
        day.Telematics = Vehicle(6, 40);

        var gaps = DepartureComplianceClassifier.GapsOf(day);
        var signals = DepartureComplianceClassifier.SignalsOf(day);

        Assert.Equal(Gaps.None, gaps);
        Assert.True(signals.HasFlag(Signals.DepartureDiscrepancy));
        Assert.Equal(30, day.DepartureDiscrepancyMinutes);
    }

    [Fact]
    public void A_disagreement_inside_the_tolerance_is_not_even_shown()
    {
        var day = Clean();
        day.TimeOut = Day(6, 40);
        day.Telematics = Vehicle(6, 45);

        Assert.False(DepartureComplianceClassifier.SignalsOf(day)
            .HasFlag(Signals.DepartureDiscrepancy));
    }

    [Theory]
    [InlineData(TelematicsMatch.NoRegistration, Signals.VehicleNoRegistration)]
    [InlineData(TelematicsMatch.NotInFleet, Signals.VehicleUnmatched)]
    public void A_vehicle_that_cannot_be_looked_up_marks_the_data_not_the_rep(
        TelematicsMatch match, Signals expected)
    {
        var day = Clean();
        day.Telematics = new DepartureComplianceTelematicsDay { Match = match, Registration = "BAD1234" };

        Assert.Equal(Gaps.None, DepartureComplianceClassifier.GapsOf(day));
        Assert.True(DepartureComplianceClassifier.SignalsOf(day).HasFlag(expected));
    }

    [Fact]
    public void A_van_that_never_moved_is_a_signal_not_a_gap()
    {
        // It is as likely to be a dead tracker as an idle driver, and the report must not decide
        // which on the rep's behalf.
        var day = Clean();
        day.Telematics = new DepartureComplianceTelematicsDay
        {
            Match = TelematicsMatch.Matched,
            HasRollup = true,
            MovementRead = true
        };

        Assert.Equal(Gaps.None, DepartureComplianceClassifier.GapsOf(day));
        Assert.True(DepartureComplianceClassifier.SignalsOf(day).HasFlag(Signals.VehicleDidNotMove));
    }

    [Fact]
    public void A_small_mileage_gap_is_not_a_finding()
    {
        // The tracker accumulates metres while the rep subtracts two whole-kilometre readings,
        // so the vehicle reads slightly higher as a matter of course.
        var day = Clean();
        day.Telematics = Vehicle(6, 30, distanceKm: 213);

        Assert.False(DepartureComplianceClassifier.SignalsOf(day)
            .HasFlag(Signals.OdometerDivergence));
    }

    [Fact]
    public void A_mileage_gap_past_both_tolerances_is_shown()
    {
        var day = Clean();
        day.Telematics = Vehicle(6, 30, distanceKm: 400);

        Assert.True(DepartureComplianceClassifier.SignalsOf(day)
            .HasFlag(Signals.OdometerDivergence));
        Assert.Equal(190, day.OdometerDivergenceKm);
    }

    [Fact]
    public void A_clean_day_with_a_verified_departure_is_still_clean()
    {
        var day = Clean();
        day.TimeOut = Day(6, 40);
        day.Telematics = Vehicle(6, 42, distanceKm: 210);

        Assert.Equal(Gaps.None, DepartureComplianceClassifier.GapsOf(day));
        Assert.True(DepartureComplianceClassifier.SignalsOf(day).HasFlag(Signals.DepartureVerified));
    }

    [Fact]
    public void A_signal_never_outranks_a_gap_for_the_badge()
    {
        var flag = DepartureComplianceClassifier.PrimaryFlag(
            Gaps.ShortDeclaration, Signals.VehicleDidNotMove);

        Assert.Equal("Short declaration", flag!.Value.Label);
        Assert.Equal(FlagTone.Strong, flag.Value.Tone);
    }

    [Fact]
    public void A_signal_badge_is_drawn_quietly()
    {
        var flag = DepartureComplianceClassifier.PrimaryFlag(Gaps.None, Signals.VehicleDidNotMove);

        Assert.Equal("Vehicle never moved", flag!.Value.Label);
        Assert.Equal(FlagTone.Info, flag.Value.Tone);
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
    [InlineData(Gaps.NoDeparture, "No departure record", FlagTone.Strong)]
    [InlineData(Gaps.NothingDeclared, "Nothing declared", FlagTone.Strong)]
    [InlineData(Gaps.ShortDeclaration, "Short declaration", FlagTone.Strong)]
    [InlineData(Gaps.OverDeclaration, "Over declaration", FlagTone.Strong)]
    [InlineData(Gaps.LateOut, "Late out", FlagTone.Warn)]
    [InlineData(Gaps.NoOdometer, "No odometer", FlagTone.Warn)]
    public void Each_gap_that_carries_a_badge_carries_its_own(Gaps gap, string label, FlagTone tone)
    {
        var flag = DepartureComplianceClassifier.PrimaryFlag(gap);

        Assert.NotNull(flag);
        Assert.Equal(label, flag!.Value.Label);
        Assert.Equal(tone, flag.Value.Tone);
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
        Assert.True(all.Test(Row(Gaps.None)));
        Assert.True(all.Test(Row(Gaps.NoDeparture)));
    }

    [Fact]
    public void The_money_chip_gathers_all_three_money_findings()
    {
        var money = DepartureComplianceClassifier.Filters.Single(filter => filter.Key == "money");

        Assert.True(money.Test(Row(Gaps.NothingDeclared)));
        Assert.True(money.Test(Row(Gaps.ShortDeclaration)));
        Assert.True(money.Test(Row(Gaps.OverDeclaration)));
        Assert.False(money.Test(Row(Gaps.LateOut)));
    }

    private static ComplianceRow Row(Gaps gaps, Signals signals = Signals.None) =>
        new(Clean(), gaps, signals);

    [Fact]
    public void Every_chip_has_a_distinct_key()
    {
        var keys = DepartureComplianceClassifier.AllFilters.Select(filter => filter.Key).ToList();

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
