using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Working out when the van is next at a shop, and whether there is still time to order for it.
///
/// This is arithmetic on dates across a timezone, which is where quiet errors live. Zimbabwe is two
/// hours ahead of UTC, so between midnight and 02:00 CAT the UTC date is still yesterday: a
/// calculation done in UTC names the wrong weekday for those two hours every single day, and the
/// result — a delivery expected on Tuesday arriving Wednesday — looks like a scheduling mistake
/// rather than a bug.
///
/// The cut-off is the other half. It is what stops an order arriving after the van has been loaded,
/// so the boundary is asserted from both sides rather than assumed.
/// </summary>
public sealed class VanSalesVisitScheduleTests
{
    private const int CutOffHours = 8; // 16:00 CAT the day before

    private static readonly DayOfWeek[] TuesdayOnly = [DayOfWeek.Tuesday];
    private static readonly DayOfWeek[] TuesdayAndFriday = [DayOfWeek.Tuesday, DayOfWeek.Friday];

    [Fact]
    public void The_next_call_is_the_coming_visit_day()
    {
        // Monday 09:00 CAT, shop called on Tuesdays.
        var window = VanSalesVisitSchedule.NextOpenVisit(Cat(2026, 8, 24, 9, 0), TuesdayOnly, CutOffHours);

        Assert.True(window.HasSchedule);
        Assert.True(window.IsOrderingOpen);
        Assert.Equal(new DateTime(2026, 8, 25), window.NextVisitDate);
    }

    [Fact]
    public void Ordering_closes_at_the_cut_off_and_rolls_to_the_following_week()
    {
        // Monday 16:00 CAT is exactly the cut-off for Tuesday. The van is being loaded.
        var window = VanSalesVisitSchedule.NextOpenVisit(Cat(2026, 8, 24, 16, 0), TuesdayOnly, CutOffHours);

        Assert.True(window.IsOrderingOpen);
        Assert.Equal(new DateTime(2026, 9, 1), window.NextVisitDate);
    }

    [Fact]
    public void A_minute_before_the_cut_off_still_makes_the_coming_call()
    {
        // The other side of the same boundary. Asserted because an off-by-one here silently costs a
        // customer a week.
        var window = VanSalesVisitSchedule.NextOpenVisit(Cat(2026, 8, 24, 15, 59), TuesdayOnly, CutOffHours);

        Assert.Equal(new DateTime(2026, 8, 25), window.NextVisitDate);
    }

    [Fact]
    public void The_visit_day_itself_is_already_past_its_cut_off()
    {
        // Tuesday morning, for a Tuesday shop. The van left hours ago; the next chance is next week.
        var window = VanSalesVisitSchedule.NextOpenVisit(Cat(2026, 8, 25, 7, 0), TuesdayOnly, CutOffHours);

        Assert.Equal(new DateTime(2026, 9, 1), window.NextVisitDate);
    }

    [Fact]
    public void The_nearer_of_two_calling_days_wins()
    {
        // Wednesday, shop called on Tuesdays and Fridays.
        var window = VanSalesVisitSchedule.NextOpenVisit(Cat(2026, 8, 26, 9, 0), TuesdayAndFriday, CutOffHours);

        Assert.Equal(new DateTime(2026, 8, 28), window.NextVisitDate);
    }

    [Fact]
    public void After_the_last_call_of_the_week_it_wraps_to_the_next()
    {
        // Friday afternoon, past Friday's cut-off. Next is Tuesday.
        var window = VanSalesVisitSchedule.NextOpenVisit(Cat(2026, 8, 28, 17, 0), TuesdayAndFriday, CutOffHours);

        Assert.Equal(new DateTime(2026, 9, 1), window.NextVisitDate);
    }

    [Fact]
    public void A_call_happening_today_is_already_closed_however_early_it_is()
    {
        // 00:30 on the morning of the call. Even with the cut-off set to zero — "order right up to
        // the start of the day" — the day has started, so this order is for next week's call. The
        // van is loaded the afternoon before and nothing put in overnight can be on it.
        var window = VanSalesVisitSchedule.NextOpenVisit(
            Cat(2026, 8, 24, 0, 30),
            [DayOfWeek.Monday],
            cutOffHoursBeforeVisitDay: 0);

        Assert.Equal(new DateTime(2026, 8, 31), window.NextVisitDate);
    }

    [Fact]
    public void The_deadline_is_midnight_CAT_on_the_visit_day_not_midnight_UTC()
    {
        // Where the timezone actually bites. Zimbabwe runs two hours ahead, so CAT midnight on the
        // 25th is 22:00 UTC on the 24th. A deadline computed against UTC midnight would give
        // customers two extra hours after the van was loaded, every single call.
        var window = VanSalesVisitSchedule.NextOpenVisit(
            Cat(2026, 8, 24, 9, 0),
            [DayOfWeek.Tuesday],
            cutOffHoursBeforeVisitDay: 0);

        Assert.Equal(new DateTime(2026, 8, 25), window.NextVisitDate);
        Assert.Equal(new DateTime(2026, 8, 24, 22, 0, 0), window.OrdersCloseAtUtc);
    }

    [Fact]
    public void A_shop_with_no_calling_days_can_still_order()
    {
        // Master data is incomplete for most customers on day one. Refusing them would make the
        // app useless exactly when it is being rolled out.
        var window = VanSalesVisitSchedule.NextOpenVisit(Cat(2026, 8, 24, 9, 0), [], CutOffHours);

        Assert.False(window.HasSchedule);
        Assert.True(window.IsOrderingOpen);
        Assert.Null(window.NextVisitDate);
        Assert.Null(window.OrdersCloseAtUtc);
    }

    [Fact]
    public void A_sunday_call_is_a_calling_day_like_any_other()
    {
        // DayOfWeek numbers Sunday as 0. Anything that treated the week as starting on Monday would
        // drop this one.
        var window = VanSalesVisitSchedule.NextOpenVisit(
            Cat(2026, 8, 27, 9, 0),
            [DayOfWeek.Sunday],
            CutOffHours);

        Assert.Equal(new DateTime(2026, 8, 30), window.NextVisitDate);
    }

    [Fact]
    public void The_cut_off_instant_is_reported_so_the_app_can_show_a_deadline()
    {
        var window = VanSalesVisitSchedule.NextOpenVisit(Cat(2026, 8, 24, 9, 0), TuesdayOnly, CutOffHours);

        Assert.NotNull(window.OrdersCloseAtUtc);

        // 16:00 CAT on Monday the 24th, expressed in UTC.
        Assert.Equal(AuditService.FromCAT(new DateTime(2026, 8, 24, 16, 0, 0)), window.OrdersCloseAtUtc);
    }

    [Fact]
    public void A_zero_cut_off_keeps_ordering_open_until_the_day_starts()
    {
        var window = VanSalesVisitSchedule.NextOpenVisit(
            Cat(2026, 8, 24, 23, 30),
            [DayOfWeek.Tuesday],
            cutOffHoursBeforeVisitDay: 0);

        Assert.Equal(new DateTime(2026, 8, 25), window.NextVisitDate);
    }

    // ── Checking a date an order names for itself ───────

    [Fact]
    public void An_order_for_a_day_the_shop_is_not_called_on_is_not_open()
    {
        // An offline order can name a date chosen days earlier, so the date it asks for is what has
        // to be checked — not whatever happens to be next when it finally reaches the server.
        var open = VanSalesVisitSchedule.IsOpenForVisitDate(
            Cat(2026, 8, 24, 9, 0),
            new DateTime(2026, 8, 26),
            TuesdayOnly,
            CutOffHours);

        Assert.False(open);
    }

    [Fact]
    public void An_order_for_a_call_whose_cut_off_has_passed_is_not_open()
    {
        var open = VanSalesVisitSchedule.IsOpenForVisitDate(
            Cat(2026, 8, 24, 17, 0),
            new DateTime(2026, 8, 25),
            TuesdayOnly,
            CutOffHours);

        Assert.False(open);
    }

    [Fact]
    public void An_order_for_the_coming_call_before_its_cut_off_is_open()
    {
        var open = VanSalesVisitSchedule.IsOpenForVisitDate(
            Cat(2026, 8, 24, 9, 0),
            new DateTime(2026, 8, 25),
            TuesdayOnly,
            CutOffHours);

        Assert.True(open);
    }

    [Fact]
    public void With_no_schedule_any_future_date_before_its_cut_off_is_open()
    {
        // Same reasoning as the unscheduled shop above: the cut-off still applies, the weekday
        // cannot.
        var open = VanSalesVisitSchedule.IsOpenForVisitDate(
            Cat(2026, 8, 24, 9, 0),
            new DateTime(2026, 8, 26),
            [],
            CutOffHours);

        Assert.True(open);
    }

    /// <summary>A CAT wall-clock moment, as the instant it actually is.</summary>
    private static DateTime Cat(int year, int month, int day, int hour, int minute) =>
        AuditService.FromCAT(new DateTime(year, month, day, hour, minute, 0));
}
