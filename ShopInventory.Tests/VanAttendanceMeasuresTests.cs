using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The measures the attendance report adds on top of what the API sends: time on the clock, the
/// on-site share taken from it, and the idle gaps between calls.
///
/// They live on the portal model rather than in the query for two different reasons, and the
/// distinction is worth keeping straight. The clock and the share are arithmetic over figures the
/// API already sends, so computing them twice is what would let them disagree. The idle gaps are a
/// *rule* — "how long is a long gap" is the reader's to set, and the page has to be able to answer
/// for a different threshold without a round trip.
///
/// What every test here is really guarding is one rule: **a measure with no denominator is
/// unavailable, not zero.** A rep whose day never closed has no time on the clock. Reporting that
/// as 0% would read as a rep who visited nobody, which is an accusation rather than a gap in the
/// data, and it is the same rule the departure compliance sheet applies to CCR.
/// </summary>
public sealed class VanAttendanceMeasuresTests
{
    private static readonly DateTime Morning = new(2026, 8, 12, 06, 00, 0, DateTimeKind.Utc);

    // ── Time on the clock ───────────────────────────────────────────────────

    [Fact]
    public void A_day_is_on_the_clock_from_the_first_check_in_to_the_last_check_out()
    {
        var day = GivenDay(
            Call(Morning, Morning.AddMinutes(30)),
            Call(Morning.AddHours(4), Morning.AddHours(4).AddMinutes(20)));

        Assert.Equal(4 * 60 + 20, day.ClockMinutes);
    }

    /// <summary>
    /// A day whose last call was never closed has no end, so no length. Taking "now" as the end
    /// would make the rep's on-site share fall for every hour the report was left open.
    /// </summary>
    [Fact]
    public void A_day_that_never_closed_has_no_clock_and_no_share()
    {
        var day = GivenDay(Call(Morning, checkOut: null));

        Assert.Null(day.ClockMinutes);
        Assert.Null(day.OnSiteShare);
    }

    [Fact]
    public void The_share_is_time_on_site_over_time_on_the_clock()
    {
        var day = GivenDay(
            Call(Morning, Morning.AddHours(1)),
            Call(Morning.AddHours(3), Morning.AddHours(4)));

        // Two hours with customers inside a four-hour day.
        Assert.Equal(120, day.TotalMinutes);
        Assert.Equal(240, day.ClockMinutes);
        Assert.Equal(0.5, day.OnSiteShare);
    }

    /// <summary>
    /// One call, checked in and out at the same instant: a day with no length. Null rather than a
    /// division by zero, and null rather than 0%.
    /// </summary>
    [Fact]
    public void A_day_with_no_length_has_no_share()
    {
        var day = GivenDay(Call(Morning, Morning));

        Assert.Null(day.ClockMinutes);
        Assert.Null(day.OnSiteShare);
    }

    // ── The rep's period ────────────────────────────────────────────────────

    /// <summary>
    /// The measure that would be wrong if it were taken across the period rather than per day:
    /// two days a fortnight apart, each four hours long, are eight hours on the clock — not the
    /// fourteen days between the first check-in and the last check-out.
    /// </summary>
    [Fact]
    public void A_reps_clock_sums_their_days_rather_than_spanning_them()
    {
        var rep = new VanVisitReportRepSummary
        {
            TotalMinutes = 240,
            Days =
            [
                GivenDay(Call(Morning, Morning.AddHours(4))),
                GivenDay(Call(Morning.AddDays(14), Morning.AddDays(14).AddHours(4)))
            ]
        };

        Assert.Equal(8 * 60, rep.ClockMinutes);
        Assert.Equal(0.5, rep.OnSiteShare);
    }

    /// <summary>
    /// A rep whose every day is still open has no clock at all. The day count is not the question —
    /// there is nothing to be a share of.
    /// </summary>
    [Fact]
    public void A_rep_with_no_closed_day_has_no_share()
    {
        var rep = new VanVisitReportRepSummary
        {
            TotalMinutes = 0,
            Days = [GivenDay(Call(Morning, checkOut: null))]
        };

        Assert.Equal(0, rep.ClockMinutes);
        Assert.Null(rep.OnSiteShare);
    }

    // ── Idle gaps ───────────────────────────────────────────────────────────

    [Fact]
    public void A_gap_at_or_over_the_threshold_is_idle_and_a_shorter_one_is_not()
    {
        var day = GivenDay(
            Call(Morning, Morning.AddMinutes(30)),
            // 45 minutes later — exactly the threshold, and counted.
            Call(Morning.AddMinutes(75), Morning.AddMinutes(90)),
            // 20 minutes later — driving between two shops, not idling.
            Call(Morning.AddMinutes(110), Morning.AddMinutes(130)));

        var gap = Assert.Single(day.IdleGaps(45));
        Assert.Equal(45, gap.Minutes);
        Assert.Equal(Morning.AddMinutes(30), gap.FromUtc);
        Assert.Equal(Morning.AddMinutes(75), gap.ToUtc);
    }

    /// <summary>
    /// The threshold is the reader's, so the same day answers differently for a different one.
    /// This is why the gaps are not computed once by the API.
    /// </summary>
    [Fact]
    public void The_same_day_answers_a_lower_threshold_differently()
    {
        var day = GivenDay(
            Call(Morning, Morning.AddMinutes(30)),
            Call(Morning.AddMinutes(60), Morning.AddMinutes(90)));

        Assert.Empty(day.IdleGaps(45));
        Assert.Single(day.IdleGaps(20));
    }

    /// <summary>
    /// The interval after a call the rep never checked out of is unmeasurable — they may have been
    /// inside the shop for all of it. Charging it as idle turns one missing tap into an accusation.
    /// </summary>
    [Fact]
    public void The_stretch_after_an_open_call_is_never_charged_as_idle()
    {
        var day = GivenDay(
            Call(Morning, checkOut: null),
            Call(Morning.AddHours(5), Morning.AddHours(5).AddMinutes(20)));

        Assert.Empty(day.IdleGaps(45));
    }

    [Fact]
    public void A_day_with_one_call_has_no_gaps_to_find()
    {
        Assert.Empty(GivenDay(Call(Morning, Morning.AddMinutes(30))).IdleGaps(45));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A day built the way the API sends one: the totals derived from the calls, so a test cannot
    /// state a total that its own calls contradict.
    /// </summary>
    private static VanVisitReportDaySummary GivenDay(params VanVisitReportCallSummary[] calls) =>
        new()
        {
            Date = Morning.Date,
            Calls = [.. calls],
            CallCount = calls.Length,
            OpenCalls = calls.Count(call => call.CheckOutTime is null),
            TotalMinutes = calls.Sum(call => call.DurationMinutes ?? 0),
            FirstCheckIn = calls.Length > 0 ? calls.Min(call => call.CheckInTime) : null,
            LastCheckOut = calls.Any(call => call.CheckOutTime.HasValue)
                ? calls.Where(call => call.CheckOutTime.HasValue).Max(call => call.CheckOutTime)
                : null
        };

    private static VanVisitReportCallSummary Call(DateTime checkIn, DateTime? checkOut) => new()
    {
        CustomerCode = "SHOP1",
        CustomerName = "Shop One",
        CheckInTime = checkIn,
        CheckOutTime = checkOut
    };
}
