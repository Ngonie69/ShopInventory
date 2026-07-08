using Quartz;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

/// <summary>
/// Quartz job that sends any due scheduled POD report emails. Runs on a 1-minute interval
/// trigger (see WebQuartzConfiguration) so schedules can be honoured to the minute; clustering
/// ensures only one Web node sends, avoiding duplicate emails.
///
/// Due times are computed against the wall clock in the business timezone (CAT) and only
/// converted to UTC at the end, so a "Monday 06:00" schedule fires at 06:00 CAT no matter what
/// timezone the server runs in. The catch-up "is this schedule due?" logic is unchanged: the most
/// recent due instant is compared against the last send (or the anchor, for never-sent schedules).
/// </summary>
[DisallowConcurrentExecution]
public sealed class PodReportEmailJob(
    IServiceScopeFactory scopeFactory,
    ILogger<PodReportEmailJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context) => ProcessAsync(context.CancellationToken);

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        // Master toggle gates all scheduled sending; individual schedules also have their own Enabled flag.
        var masterEnabled = ParseBool(await settingsService.GetValueAsync(SettingKeys.PodReportEmailsEnabled));
        if (!masterEnabled)
        {
            return;
        }

        var scheduleService = scope.ServiceProvider.GetRequiredService<IPodReportEmailScheduleService>();
        var schedules = await scheduleService.GetSchedulesAsync(cancellationToken);
        if (schedules.Count == 0)
        {
            return;
        }

        var reportEmailService = scope.ServiceProvider.GetRequiredService<IPodReportEmailService>();
        var nowLocal = PodScheduleTime.NowLocal();

        foreach (var schedule in schedules)
        {
            if (!schedule.Enabled)
            {
                continue;
            }

            var frequency = PodReportEmailService.ParseFrequency(schedule.Frequency);
            var dueLocal = ComputeMostRecentDueLocal(schedule, frequency, nowLocal);
            if (dueLocal is null)
            {
                // Schedule's first occurrence hasn't arrived yet.
                continue;
            }

            var dueUtc = PodScheduleTime.ToUtc(dueLocal.Value);

            // Floor at the last send, or the anchor for never-sent schedules so a brand-new
            // schedule doesn't immediately fire for an already-elapsed period.
            var floorUtc = schedule.LastSentUtc ?? schedule.AnchorDateUtc;

            if (dueUtc <= floorUtc)
            {
                continue;
            }

            await SendScheduledReportAsync(reportEmailService, scheduleService, schedule, dueUtc, cancellationToken);
        }
    }

    private async Task SendScheduledReportAsync(
        IPodReportEmailService reportEmailService,
        IPodReportEmailScheduleService scheduleService,
        PodReportEmailSchedule schedule,
        DateTime dueUtc,
        CancellationToken cancellationToken)
    {
        var result = await reportEmailService.SendForScheduleAsync(schedule, "System schedule", cancellationToken);

        if (!result.Success)
        {
            logger.LogWarning(
                "Scheduled POD report email failed for schedule {ScheduleName} (#{ScheduleId}): {Message}",
                schedule.Name,
                schedule.Id,
                result.Message);
            return;
        }

        await scheduleService.MarkSentAsync(schedule.Id, dueUtc, cancellationToken);
        logger.LogInformation(
            "Scheduled POD report email sent for schedule {ScheduleName} (#{ScheduleId}).",
            schedule.Name,
            schedule.Id);
    }

    /// <summary>
    /// Most recent local instant at which this schedule should have fired, or null when the
    /// schedule's first occurrence is still in the future.
    /// </summary>
    internal static DateTime? ComputeMostRecentDueLocal(
        PodReportEmailSchedule schedule,
        PodReportEmailFrequency frequency,
        DateTime nowLocal)
    {
        var minuteOfDay = PodScheduleTime.NormalizeMinuteOfDay(schedule.SendMinuteOfDay);

        return frequency switch
        {
            PodReportEmailFrequency.Daily => GetMostRecentDailyLocal(nowLocal, minuteOfDay),
            PodReportEmailFrequency.Weekly => GetMostRecentWeeklyLocal(nowLocal, ResolveDayOfWeek(schedule.DayOfWeek), minuteOfDay),
            PodReportEmailFrequency.Monthly => GetMostRecentMonthlyLocal(nowLocal, schedule.DayOfMonth ?? 1, minuteOfDay),
            PodReportEmailFrequency.EveryNDays => GetMostRecentEveryNDaysLocal(
                nowLocal,
                PodScheduleTime.ToLocal(DateTime.SpecifyKind(schedule.AnchorDateUtc, DateTimeKind.Utc)),
                PodReportEmailService.NormalizeIntervalDays(schedule.IntervalDays),
                minuteOfDay),
            _ => GetMostRecentWeeklyLocal(nowLocal, ResolveDayOfWeek(schedule.DayOfWeek), minuteOfDay)
        };
    }

    private static DayOfWeek ResolveDayOfWeek(int? dayOfWeek)
    {
        var value = Math.Clamp(dayOfWeek ?? (int)DayOfWeek.Monday, 0, 6);
        return (DayOfWeek)value;
    }

    private static DateTime GetMostRecentDailyLocal(DateTime nowLocal, int minuteOfDay)
    {
        var scheduled = nowLocal.Date.AddMinutes(minuteOfDay);
        if (scheduled > nowLocal)
        {
            scheduled = scheduled.AddDays(-1);
        }

        return scheduled;
    }

    private static DateTime GetMostRecentWeeklyLocal(DateTime nowLocal, DayOfWeek targetDay, int minuteOfDay)
    {
        var dayOffset = (int)targetDay - (int)nowLocal.DayOfWeek;
        var scheduled = nowLocal.Date.AddDays(dayOffset).AddMinutes(minuteOfDay);

        if (scheduled > nowLocal)
        {
            scheduled = scheduled.AddDays(-7);
        }

        return scheduled;
    }

    private static DateTime GetMostRecentMonthlyLocal(DateTime nowLocal, int dayOfMonth, int minuteOfDay)
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

    private static DateTime BuildMonthlyLocal(int year, int month, int dayOfMonth, int minuteOfDay)
    {
        // Clamp to the last day of a short month (e.g. "day 31" in February).
        var day = Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified).AddMinutes(minuteOfDay);
    }

    private static DateTime? GetMostRecentEveryNDaysLocal(
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

    private static bool ParseBool(string? value) => bool.TryParse(value, out var parsed) && parsed;
}
