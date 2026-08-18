namespace ShopInventory.Web.Services;

/// <summary>
/// The frequencies a recurring report email can run on.
/// </summary>
/// <remarks>
/// Deliberately a separate enum from any one report's own. The cadence is shared and the reports
/// are not, so a report that later grows a frequency nobody else has should not silently widen
/// every other report's settings screen. <c>ReportScheduleCadenceTests</c> asserts the member names
/// still line up with the POD enum, which is what the mapping between them relies on.
/// </remarks>
public enum ReportScheduleFrequency
{
    Weekly,
    Monthly,
    Daily,
    MonthToDateDaily,
    EveryNDays,
    Quarterly,
    HalfYearly
}

/// <summary>
/// When a recurring report email is next due, and when it last should have gone out.
/// </summary>
/// <remarks>
/// Lifted out of <see cref="PodReportEmailJob"/> unchanged when a second report needed the same
/// cadence. Every function below is the POD job's own arithmetic, moved verbatim and only made
/// visible — a second implementation of "when is a monthly schedule due" is the kind of duplication
/// nobody notices until one report arrives on the wrong day and the other does not.
///
/// The extraction was made safe before it was made: <c>ReportScheduleCadenceTests</c> was written
/// against the POD job's behaviour first, while the arithmetic was still private to it, and the same
/// tests pass unchanged against this. The cadence had no test at all before that.
///
/// <b>Everything here is wall-clock local time, never UTC.</b> A schedule that says "Monday 06:00"
/// means Monday 06:00 in the shop whatever timezone the server runs in, so the whole calculation is
/// done in local time and only the caller converts the answer. Doing any part of it in UTC would be
/// correct in testing and an hour or two wrong in the field.
/// </remarks>
public static class ReportScheduleCadence
{
    /// <summary>
    /// One schedule's shape, independent of which report it sends.
    /// </summary>
    /// <remarks>
    /// A record rather than the schedule entity, so the cadence does not depend on any one report's
    /// table — which is the whole reason it could be shared in the first place.
    /// </remarks>
    public sealed record Rule(
        ReportScheduleFrequency Frequency,
        int SendMinuteOfDay,
        int? DayOfWeek = null,
        int? DayOfMonth = null,
        int? IntervalDays = null,
        DateTime? AnchorUtc = null);

    /// <summary>
    /// The most recent occurrence that has already passed, or null when the first has not arrived.
    /// </summary>
    /// <remarks>
    /// Null rather than a date in the past, and the distinction is load-bearing: a schedule saved
    /// this morning must not immediately fire for a period it was never meant to cover. That is the
    /// one bug here that would mail every recipient the moment somebody pressed save.
    /// </remarks>
    public static DateTime? MostRecentDueLocal(Rule rule, DateTime nowLocal)
    {
        var minuteOfDay = PodScheduleTime.NormalizeMinuteOfDay(rule.SendMinuteOfDay);

        return rule.Frequency switch
        {
            ReportScheduleFrequency.Daily => GetMostRecentDailyLocal(nowLocal, minuteOfDay),
            ReportScheduleFrequency.MonthToDateDaily => GetMostRecentDailyLocal(nowLocal, minuteOfDay),
            ReportScheduleFrequency.Weekly =>
                GetMostRecentWeeklyLocal(nowLocal, ResolveDayOfWeek(rule.DayOfWeek), minuteOfDay),
            ReportScheduleFrequency.Monthly =>
                GetMostRecentMonthlyLocal(nowLocal, rule.DayOfMonth ?? 1, minuteOfDay),
            ReportScheduleFrequency.Quarterly =>
                GetMostRecentPeriodicLocal(nowLocal, rule.DayOfMonth ?? 1, minuteOfDay, 3),
            ReportScheduleFrequency.HalfYearly =>
                GetMostRecentPeriodicLocal(nowLocal, rule.DayOfMonth ?? 1, minuteOfDay, 6),
            ReportScheduleFrequency.EveryNDays => GetMostRecentEveryNDaysLocal(
                nowLocal,
                AnchorLocal(rule),
                NormalizeIntervalDays(rule.IntervalDays),
                minuteOfDay),
            _ => GetMostRecentWeeklyLocal(nowLocal, ResolveDayOfWeek(rule.DayOfWeek), minuteOfDay)
        };
    }

    /// <summary>The next occurrence still to come. Always a date — every frequency has one.</summary>
    public static DateTime NextDueLocal(Rule rule, DateTime nowLocal)
    {
        var minuteOfDay = PodScheduleTime.NormalizeMinuteOfDay(rule.SendMinuteOfDay);

        return rule.Frequency switch
        {
            ReportScheduleFrequency.Daily => GetNextDailyLocal(nowLocal, minuteOfDay),
            ReportScheduleFrequency.MonthToDateDaily => GetNextDailyLocal(nowLocal, minuteOfDay),
            ReportScheduleFrequency.Weekly =>
                GetNextWeeklyLocal(nowLocal, ResolveDayOfWeek(rule.DayOfWeek), minuteOfDay),
            ReportScheduleFrequency.Monthly =>
                GetNextMonthlyLocal(nowLocal, rule.DayOfMonth ?? 1, minuteOfDay),
            ReportScheduleFrequency.Quarterly =>
                GetNextPeriodicLocal(nowLocal, rule.DayOfMonth ?? 1, minuteOfDay, 3),
            ReportScheduleFrequency.HalfYearly =>
                GetNextPeriodicLocal(nowLocal, rule.DayOfMonth ?? 1, minuteOfDay, 6),
            ReportScheduleFrequency.EveryNDays => GetNextEveryNDaysLocal(
                nowLocal,
                AnchorLocal(rule),
                NormalizeIntervalDays(rule.IntervalDays),
                minuteOfDay),
            _ => GetNextWeeklyLocal(nowLocal, ResolveDayOfWeek(rule.DayOfWeek), minuteOfDay)
        };
    }

    /// <summary>
    /// Whether a schedule is due to send now.
    /// </summary>
    /// <remarks>
    /// The rule both jobs apply, in one place: send when the most recent occurrence is later than
    /// the last send — or later than the anchor where nothing has ever been sent, so a new schedule
    /// does not fire for an already-elapsed period.
    /// </remarks>
    public static bool IsDue(Rule rule, DateTime nowLocal, DateTime? lastSentUtc, DateTime anchorUtc)
    {
        var dueLocal = MostRecentDueLocal(rule, nowLocal);

        if (dueLocal is null)
        {
            return false;
        }

        return PodScheduleTime.ToUtc(dueLocal.Value) > (lastSentUtc ?? anchorUtc);
    }

    /// <summary>At least a day, so an unset or nonsensical interval cannot mean "send continuously".</summary>
    public static int NormalizeIntervalDays(int? intervalDays) => Math.Max(1, intervalDays ?? 1);

    private static DateTime AnchorLocal(Rule rule) =>
        PodScheduleTime.ToLocal(
            DateTime.SpecifyKind(rule.AnchorUtc ?? DateTime.UtcNow, DateTimeKind.Utc));

    internal static DayOfWeek ResolveDayOfWeek(int? dayOfWeek)
    {
        var value = Math.Clamp(dayOfWeek ?? (int)DayOfWeek.Monday, 0, 6);
        return (DayOfWeek)value;
    }

    internal static DateTime GetMostRecentDailyLocal(DateTime nowLocal, int minuteOfDay)
    {
        var scheduled = nowLocal.Date.AddMinutes(minuteOfDay);
        if (scheduled > nowLocal)
        {
            scheduled = scheduled.AddDays(-1);
        }

        return scheduled;
    }

    internal static DateTime GetNextDailyLocal(DateTime nowLocal, int minuteOfDay)
    {
        var scheduled = nowLocal.Date.AddMinutes(minuteOfDay);
        return scheduled > nowLocal ? scheduled : scheduled.AddDays(1);
    }

    internal static DateTime GetMostRecentWeeklyLocal(DateTime nowLocal, DayOfWeek targetDay, int minuteOfDay)
    {
        var dayOffset = (int)targetDay - (int)nowLocal.DayOfWeek;
        var scheduled = nowLocal.Date.AddDays(dayOffset).AddMinutes(minuteOfDay);

        if (scheduled > nowLocal)
        {
            scheduled = scheduled.AddDays(-7);
        }

        return scheduled;
    }

    internal static DateTime GetNextWeeklyLocal(DateTime nowLocal, DayOfWeek targetDay, int minuteOfDay)
    {
        var dayOffset = (int)targetDay - (int)nowLocal.DayOfWeek;
        var scheduled = nowLocal.Date.AddDays(dayOffset).AddMinutes(minuteOfDay);
        return scheduled > nowLocal ? scheduled : scheduled.AddDays(7);
    }

    internal static DateTime GetMostRecentMonthlyLocal(DateTime nowLocal, int dayOfMonth, int minuteOfDay)
    {
        var clampedDay = Math.Clamp(dayOfMonth, 1, 31);
        var scheduled = BuildMonthlyLocal(nowLocal.Year, nowLocal.Month, clampedDay, minuteOfDay);

        if (scheduled > nowLocal)
        {
            var previousMonth = nowLocal.AddMonths(-1);
            scheduled = BuildMonthlyLocal(previousMonth.Year, previousMonth.Month, clampedDay, minuteOfDay);
        }

        return scheduled;
    }

    internal static DateTime GetNextMonthlyLocal(DateTime nowLocal, int dayOfMonth, int minuteOfDay)
    {
        var clampedDay = Math.Clamp(dayOfMonth, 1, 31);
        var scheduled = BuildMonthlyLocal(nowLocal.Year, nowLocal.Month, clampedDay, minuteOfDay);
        if (scheduled <= nowLocal)
        {
            var nextMonth = nowLocal.AddMonths(1);
            scheduled = BuildMonthlyLocal(nextMonth.Year, nextMonth.Month, clampedDay, minuteOfDay);
        }

        return scheduled;
    }

    internal static DateTime BuildMonthlyLocal(int year, int month, int dayOfMonth, int minuteOfDay)
    {
        // Clamp to the last day of a short month (e.g. "day 31" in February).
        var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified).AddMinutes(minuteOfDay);
    }

    internal static DateTime GetMostRecentPeriodicLocal(
        DateTime nowLocal,
        int dayOfMonth,
        int minuteOfDay,
        int monthsPerPeriod)
    {
        var periodStart = GetCurrentPeriodStart(nowLocal, monthsPerPeriod);
        var scheduled = BuildMonthlyLocal(periodStart.Year, periodStart.Month, Math.Clamp(dayOfMonth, 1, 31), minuteOfDay);
        if (scheduled > nowLocal)
        {
            var previousPeriod = periodStart.AddMonths(-monthsPerPeriod);
            scheduled = BuildMonthlyLocal(previousPeriod.Year, previousPeriod.Month, Math.Clamp(dayOfMonth, 1, 31), minuteOfDay);
        }

        return scheduled;
    }

    internal static DateTime GetNextPeriodicLocal(
        DateTime nowLocal,
        int dayOfMonth,
        int minuteOfDay,
        int monthsPerPeriod)
    {
        var periodStart = GetCurrentPeriodStart(nowLocal, monthsPerPeriod);
        var scheduled = BuildMonthlyLocal(periodStart.Year, periodStart.Month, Math.Clamp(dayOfMonth, 1, 31), minuteOfDay);
        if (scheduled <= nowLocal)
        {
            var nextPeriod = periodStart.AddMonths(monthsPerPeriod);
            scheduled = BuildMonthlyLocal(nextPeriod.Year, nextPeriod.Month, Math.Clamp(dayOfMonth, 1, 31), minuteOfDay);
        }

        return scheduled;
    }

    internal static DateTime GetCurrentPeriodStart(DateTime value, int monthsPerPeriod)
    {
        var startMonth = ((value.Month - 1) / monthsPerPeriod) * monthsPerPeriod + 1;
        return new DateTime(value.Year, startMonth, 1, 0, 0, 0, DateTimeKind.Unspecified);
    }

    internal static DateTime? GetMostRecentEveryNDaysLocal(
        DateTime nowLocal,
        DateTime anchorLocal,
        int intervalDays,
        int minuteOfDay)
    {
        var anchorInstant = anchorLocal.Date.AddMinutes(minuteOfDay);
        if (nowLocal < anchorInstant)
        {
            // First occurrence has not happened yet.
            return null;
        }

        var daysSince = (nowLocal.Date - anchorInstant.Date).Days;
        var periodsElapsed = daysSince / intervalDays;
        var scheduled = anchorInstant.AddDays(periodsElapsed * intervalDays);

        if (scheduled > nowLocal)
        {
            scheduled = scheduled.AddDays(-intervalDays);
        }

        return scheduled;
    }

    internal static DateTime GetNextEveryNDaysLocal(
        DateTime nowLocal,
        DateTime anchorLocal,
        int intervalDays,
        int minuteOfDay)
    {
        var anchorInstant = anchorLocal.Date.AddMinutes(minuteOfDay);
        if (nowLocal < anchorInstant)
        {
            return anchorInstant;
        }

        var daysSince = (nowLocal.Date - anchorInstant.Date).Days;
        var periodsElapsed = daysSince / intervalDays;
        var scheduled = anchorInstant.AddDays(periodsElapsed * intervalDays);

        return scheduled > nowLocal ? scheduled : scheduled.AddDays(intervalDays);
    }
}
