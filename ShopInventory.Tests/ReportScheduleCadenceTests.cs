using ShopInventory.Web.Data;
using ShopInventory.Web.Services;


namespace ShopInventory.Tests;

/// <summary>
/// Pins the schedule cadence — when a recurring report email is due.
///
/// These were written against the POD job's own arithmetic before that arithmetic was moved
/// anywhere, so they describe the behaviour as it already shipped rather than the behaviour anyone
/// intended. That order matters: the cadence had no test at all, and a second report was about to
/// start depending on it. An extraction proved only by "it still compiles" is not proved.
///
/// Everything here is wall-clock CAT. A schedule says "Monday 06:00" and means Monday 06:00 in the
/// shop, whatever timezone the server runs in, so the arithmetic is done entirely in local time and
/// only the persisted last-sent instant is UTC. Testing it in UTC would pass while the estate got
/// its Monday report on Sunday evening.
///
/// The distinction the whole mechanism turns on is between "the most recent occurrence that has
/// already passed" and "the next one still to come". The job sends when the most recent occurrence
/// is later than the last send — so an off-by-one in either direction either sends nothing for a
/// week or sends the same report every time the job ticks.
/// </summary>
public class ReportScheduleCadenceTests
{
    private static PodReportEmailSchedule Schedule(
        PodReportEmailFrequency frequency,
        int minuteOfDay = 6 * 60,
        int? dayOfWeek = null,
        int? dayOfMonth = null,
        int? intervalDays = null,
        DateTime? anchorUtc = null) =>
        new()
        {
            Frequency = frequency.ToString(),
            SendMinuteOfDay = minuteOfDay,
            DayOfWeek = dayOfWeek,
            DayOfMonth = dayOfMonth,
            IntervalDays = intervalDays,
            AnchorDateUtc = anchorUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    private static DateTime? MostRecent(
        PodReportEmailSchedule schedule,
        PodReportEmailFrequency frequency,
        DateTime nowLocal) =>
        PodReportEmailJob.ComputeMostRecentDueLocal(schedule, frequency, nowLocal);

    private static DateTime Next(
        PodReportEmailSchedule schedule,
        PodReportEmailFrequency frequency,
        DateTime nowLocal) =>
        PodReportEmailJob.ComputeNextDueLocal(schedule, frequency, nowLocal);

    // ── Daily ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Before the send time, today's occurrence has not happened — the most recent is yesterday's.
    /// Returning today's would make the job send a report hours early, every day.
    /// </summary>
    [Fact]
    public void Daily_before_the_send_time_the_most_recent_is_yesterday()
    {
        var schedule = Schedule(PodReportEmailFrequency.Daily, minuteOfDay: 6 * 60);
        var nowLocal = new DateTime(2026, 8, 18, 5, 30, 0);

        Assert.Equal(new DateTime(2026, 8, 17, 6, 0, 0), MostRecent(schedule, PodReportEmailFrequency.Daily, nowLocal));
        Assert.Equal(new DateTime(2026, 8, 18, 6, 0, 0), Next(schedule, PodReportEmailFrequency.Daily, nowLocal));
    }

    [Fact]
    public void Daily_after_the_send_time_the_most_recent_is_today()
    {
        var schedule = Schedule(PodReportEmailFrequency.Daily, minuteOfDay: 6 * 60);
        var nowLocal = new DateTime(2026, 8, 18, 6, 30, 0);

        Assert.Equal(new DateTime(2026, 8, 18, 6, 0, 0), MostRecent(schedule, PodReportEmailFrequency.Daily, nowLocal));
        Assert.Equal(new DateTime(2026, 8, 19, 6, 0, 0), Next(schedule, PodReportEmailFrequency.Daily, nowLocal));
    }

    /// <summary>
    /// Exactly on the minute counts as arrived. The job ticks on an interval and would otherwise
    /// skip a send whose tick landed precisely on it.
    /// </summary>
    [Fact]
    public void Daily_exactly_on_the_send_minute_counts_as_arrived()
    {
        var schedule = Schedule(PodReportEmailFrequency.Daily, minuteOfDay: 6 * 60);
        var nowLocal = new DateTime(2026, 8, 18, 6, 0, 0);

        Assert.Equal(nowLocal, MostRecent(schedule, PodReportEmailFrequency.Daily, nowLocal));
    }

    /// <summary>Month-to-date daily runs on the daily cadence; only its content differs.</summary>
    [Fact]
    public void Month_to_date_daily_uses_the_daily_cadence()
    {
        var schedule = Schedule(PodReportEmailFrequency.MonthToDateDaily, minuteOfDay: 7 * 60);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        Assert.Equal(
            MostRecent(schedule, PodReportEmailFrequency.Daily, nowLocal),
            MostRecent(schedule, PodReportEmailFrequency.MonthToDateDaily, nowLocal));
    }

    // ── Weekly ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 18 August 2026 is a Tuesday. A Monday schedule's most recent occurrence is yesterday, and its
    /// next is a week away.
    /// </summary>
    [Fact]
    public void Weekly_finds_the_occurrence_earlier_this_week()
    {
        var schedule = Schedule(PodReportEmailFrequency.Weekly, dayOfWeek: (int)DayOfWeek.Monday);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        Assert.Equal(new DateTime(2026, 8, 17, 6, 0, 0), MostRecent(schedule, PodReportEmailFrequency.Weekly, nowLocal));
        Assert.Equal(new DateTime(2026, 8, 24, 6, 0, 0), Next(schedule, PodReportEmailFrequency.Weekly, nowLocal));
    }

    /// <summary>
    /// On the day itself but before the time, the most recent is last week's — not this morning's,
    /// which has not happened.
    /// </summary>
    [Fact]
    public void Weekly_on_the_day_but_before_the_time_looks_back_a_week()
    {
        var schedule = Schedule(PodReportEmailFrequency.Weekly, dayOfWeek: (int)DayOfWeek.Tuesday);
        var nowLocal = new DateTime(2026, 8, 18, 5, 0, 0);

        Assert.Equal(new DateTime(2026, 8, 11, 6, 0, 0), MostRecent(schedule, PodReportEmailFrequency.Weekly, nowLocal));
        Assert.Equal(new DateTime(2026, 8, 18, 6, 0, 0), Next(schedule, PodReportEmailFrequency.Weekly, nowLocal));
    }

    /// <summary>An unset day of week falls back to Monday rather than to Sunday's zero.</summary>
    [Fact]
    public void Weekly_with_no_day_set_falls_back_to_monday()
    {
        var schedule = Schedule(PodReportEmailFrequency.Weekly, dayOfWeek: null);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        Assert.Equal(
            DayOfWeek.Monday,
            MostRecent(schedule, PodReportEmailFrequency.Weekly, nowLocal)!.Value.DayOfWeek);
    }

    // ── Monthly ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Monthly_before_the_day_looks_back_to_last_month()
    {
        var schedule = Schedule(PodReportEmailFrequency.Monthly, dayOfMonth: 25);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        Assert.Equal(new DateTime(2026, 7, 25, 6, 0, 0), MostRecent(schedule, PodReportEmailFrequency.Monthly, nowLocal));
        Assert.Equal(new DateTime(2026, 8, 25, 6, 0, 0), Next(schedule, PodReportEmailFrequency.Monthly, nowLocal));
    }

    /// <summary>
    /// A schedule set to the 31st in a month that has 30 days lands on the last day rather than
    /// rolling into the next month or throwing. February is the case that breaks naive arithmetic.
    /// </summary>
    [Fact]
    public void Monthly_on_a_day_the_month_does_not_have_lands_on_its_last_day()
    {
        var schedule = Schedule(PodReportEmailFrequency.Monthly, dayOfMonth: 31);
        var nowLocal = new DateTime(2026, 2, 28, 9, 0, 0);

        var due = MostRecent(schedule, PodReportEmailFrequency.Monthly, nowLocal);

        Assert.Equal(new DateTime(2026, 2, 28, 6, 0, 0), due);
    }

    // ── Quarterly and half-yearly ───────────────────────────────────────────────

    /// <summary>
    /// Quarters run from January, so in August the current period started in July. A schedule that
    /// picked the calendar quarter's end instead would send three months late.
    /// </summary>
    [Fact]
    public void Quarterly_anchors_on_the_period_start()
    {
        var schedule = Schedule(PodReportEmailFrequency.Quarterly, dayOfMonth: 1);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        Assert.Equal(
            new DateTime(2026, 7, 1, 6, 0, 0),
            MostRecent(schedule, PodReportEmailFrequency.Quarterly, nowLocal));
        Assert.Equal(
            new DateTime(2026, 10, 1, 6, 0, 0),
            Next(schedule, PodReportEmailFrequency.Quarterly, nowLocal));
    }

    [Fact]
    public void Half_yearly_anchors_on_the_period_start()
    {
        var schedule = Schedule(PodReportEmailFrequency.HalfYearly, dayOfMonth: 1);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        Assert.Equal(
            new DateTime(2026, 7, 1, 6, 0, 0),
            MostRecent(schedule, PodReportEmailFrequency.HalfYearly, nowLocal));
        Assert.Equal(
            new DateTime(2027, 1, 1, 6, 0, 0),
            Next(schedule, PodReportEmailFrequency.HalfYearly, nowLocal));
    }

    // ── Every N days ────────────────────────────────────────────────────────────

    /// <summary>
    /// Counted from the anchor, so the cadence does not drift with when the job happens to tick.
    /// Anchor 1 August, every 7 days, now the 18th: occurrences on the 1st, 8th and 15th.
    /// </summary>
    [Fact]
    public void Every_n_days_counts_from_the_anchor()
    {
        var schedule = Schedule(
            PodReportEmailFrequency.EveryNDays,
            intervalDays: 7,
            anchorUtc: new DateTime(2026, 8, 1, 4, 0, 0, DateTimeKind.Utc));

        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        Assert.Equal(
            new DateTime(2026, 8, 15, 6, 0, 0),
            MostRecent(schedule, PodReportEmailFrequency.EveryNDays, nowLocal));
        Assert.Equal(
            new DateTime(2026, 8, 22, 6, 0, 0),
            Next(schedule, PodReportEmailFrequency.EveryNDays, nowLocal));
    }

    /// <summary>
    /// Before the first occurrence there is nothing to look back at. Null rather than a date in the
    /// past, so a schedule created today does not immediately fire for a period it did not cover —
    /// the one bug that would spam recipients the moment somebody saved a new schedule.
    /// </summary>
    [Fact]
    public void Every_n_days_before_the_first_occurrence_has_no_most_recent()
    {
        var schedule = Schedule(
            PodReportEmailFrequency.EveryNDays,
            intervalDays: 7,
            anchorUtc: new DateTime(2026, 8, 20, 4, 0, 0, DateTimeKind.Utc));

        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        Assert.Null(MostRecent(schedule, PodReportEmailFrequency.EveryNDays, nowLocal));
    }

    // ── The send decision ───────────────────────────────────────────────────────

    /// <summary>
    /// The rule the job applies: send when the most recent occurrence is later than the last send.
    /// Pinned here because it is the difference between one report a day and one per job tick.
    /// </summary>
    [Theory]
    // Last sent before this morning's occurrence: due.
    [InlineData("2026-08-17T10:00:00Z", true)]
    // Last sent after it: not due again until tomorrow.
    [InlineData("2026-08-18T05:00:00Z", false)]
    public void A_schedule_is_due_only_when_its_occurrence_is_later_than_the_last_send(
        string lastSentUtc,
        bool expectedDue)
    {
        var schedule = Schedule(PodReportEmailFrequency.Daily, minuteOfDay: 6 * 60);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        var dueLocal = MostRecent(schedule, PodReportEmailFrequency.Daily, nowLocal);
        Assert.NotNull(dueLocal);

        var dueUtc = PodScheduleTime.ToUtc(dueLocal.Value);
        var lastSent = DateTime.Parse(lastSentUtc).ToUniversalTime();

        Assert.Equal(expectedDue, dueUtc > lastSent);
    }

    // ── The shared cadence, called directly ─────────────────────────────────────

    /// <summary>
    /// A schedule saved this morning must not immediately fire for a period that has already
    /// elapsed. This is the one bug here that mails every recipient the moment somebody presses
    /// save, so it is pinned from both sides of the anchor.
    /// </summary>
    [Fact]
    public void A_newly_saved_schedule_does_not_fire_for_a_period_it_never_covered()
    {
        var rule = new ReportScheduleCadence.Rule(ReportScheduleFrequency.Daily, SendMinuteOfDay: 6 * 60);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        // Anchored after this morning's occurrence: nothing to send.
        var anchoredAfter = PodScheduleTime.ToUtc(new DateTime(2026, 8, 18, 8, 0, 0));
        Assert.False(ReportScheduleCadence.IsDue(rule, nowLocal, lastSentUtc: null, anchorUtc: anchoredAfter));

        // Anchored before it: due.
        var anchoredBefore = PodScheduleTime.ToUtc(new DateTime(2026, 8, 17, 8, 0, 0));
        Assert.True(ReportScheduleCadence.IsDue(rule, nowLocal, lastSentUtc: null, anchorUtc: anchoredBefore));
    }

    /// <summary>
    /// Once sent, the last-send instant is the floor rather than the anchor — otherwise every tick
    /// after the first send would send again.
    /// </summary>
    [Fact]
    public void A_schedule_already_sent_today_is_not_due_again()
    {
        var rule = new ReportScheduleCadence.Rule(ReportScheduleFrequency.Daily, SendMinuteOfDay: 6 * 60);
        var nowLocal = new DateTime(2026, 8, 18, 9, 0, 0);

        var anchor = PodScheduleTime.ToUtc(new DateTime(2026, 1, 1, 0, 0, 0));
        var sentThisMorning = PodScheduleTime.ToUtc(new DateTime(2026, 8, 18, 6, 1, 0));

        Assert.False(ReportScheduleCadence.IsDue(rule, nowLocal, sentThisMorning, anchor));
        Assert.True(ReportScheduleCadence.IsDue(rule, nowLocal.AddDays(1), sentThisMorning, anchor));
    }

    /// <summary>
    /// An interval of zero or null would mean "send on every tick", which is once a minute. Clamped
    /// to a day.
    /// </summary>
    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(14, 14)]
    public void An_interval_is_never_less_than_a_day(int? given, int expected) =>
        Assert.Equal(expected, ReportScheduleCadence.NormalizeIntervalDays(given));

    /// <summary>
    /// The POD schedule is mapped onto the shared cadence by enum name, so the two sets of members
    /// have to stay in step. A cast would keep compiling if either gained a member in the middle and
    /// would silently re-point every existing POD schedule at a different frequency.
    /// </summary>
    [Fact]
    public void The_pod_and_shared_frequency_enums_have_the_same_members()
    {
        Assert.Equal(
            Enum.GetNames<PodReportEmailFrequency>().OrderBy(name => name),
            Enum.GetNames<ReportScheduleFrequency>().OrderBy(name => name));
    }

    // ── The window a scheduled send covers ──────────────────────────────────────

    /// <summary>
    /// A window always ends on the last complete trading day. A report sent at 06:00 that included
    /// today would cover the three hours of trading before it went out and read as a collapse.
    /// </summary>
    [Fact]
    public void A_scheduled_window_ends_yesterday_and_never_today()
    {
        var (_, until) = VanReportEmailService.WindowFor(ReportScheduleFrequency.Weekly, 7);

        Assert.Equal(PodScheduleTime.NowLocal().Date.AddDays(-1), until);
    }

    [Theory]
    [InlineData(ReportScheduleFrequency.Daily, 7, 1)]
    [InlineData(ReportScheduleFrequency.Weekly, 7, 7)]
    [InlineData(ReportScheduleFrequency.Monthly, 7, 30)]
    [InlineData(ReportScheduleFrequency.Quarterly, 7, 90)]
    [InlineData(ReportScheduleFrequency.HalfYearly, 7, 180)]
    // Every-N-days reports on exactly the period it covers.
    [InlineData(ReportScheduleFrequency.EveryNDays, 3, 3)]
    public void A_scheduled_window_matches_its_cadence(
        ReportScheduleFrequency frequency,
        int intervalDays,
        int expectedDays)
    {
        var (from, until) = VanReportEmailService.WindowFor(frequency, intervalDays);

        Assert.Equal(expectedDays, (until - from).Days + 1);
    }

    /// <summary>Month to date is the one window that is not rolling — the name promises otherwise.</summary>
    [Fact]
    public void Month_to_date_starts_at_the_first_of_the_month()
    {
        var (from, until) = VanReportEmailService.WindowFor(ReportScheduleFrequency.MonthToDateDaily, 1);

        Assert.Equal(1, from.Day);
        Assert.Equal(until.Month, from.Month);
    }
}
