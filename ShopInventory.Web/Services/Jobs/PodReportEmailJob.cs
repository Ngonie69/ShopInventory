using Quartz;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

/// <summary>
/// Quartz job that sends any due scheduled POD report emails. Runs on a 30-minute interval
/// trigger (see WebQuartzConfiguration); clustering ensures only one Web node sends, avoiding
/// duplicate emails. The catch-up "is this schedule due?" logic is unchanged.
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
        var nowUtc = DateTime.UtcNow;

        foreach (var schedule in schedules)
        {
            if (!schedule.Enabled)
            {
                continue;
            }

            var frequency = PodReportEmailService.ParseFrequency(schedule.Frequency);
            var dueUtc = ComputeMostRecentDueUtc(schedule, frequency, nowUtc);

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

    private static DateTime ComputeMostRecentDueUtc(
        PodReportEmailSchedule schedule,
        PodReportEmailFrequency frequency,
        DateTime nowUtc)
    {
        var hour = Math.Clamp(schedule.SendHourUtc, 0, 23);

        return frequency switch
        {
            PodReportEmailFrequency.Daily => GetMostRecentDailyScheduleUtc(nowUtc, hour),
            PodReportEmailFrequency.Weekly => GetMostRecentWeeklyScheduleUtc(nowUtc, ResolveDayOfWeek(schedule.DayOfWeek), hour),
            PodReportEmailFrequency.Monthly => GetMostRecentMonthlyScheduleUtc(nowUtc, schedule.DayOfMonth ?? 1, hour),
            PodReportEmailFrequency.EveryNDays => GetMostRecentEveryNDaysScheduleUtc(
                nowUtc,
                schedule.AnchorDateUtc,
                PodReportEmailService.NormalizeIntervalDays(schedule.IntervalDays),
                hour),
            _ => GetMostRecentWeeklyScheduleUtc(nowUtc, ResolveDayOfWeek(schedule.DayOfWeek), hour)
        };
    }

    private static DayOfWeek ResolveDayOfWeek(int? dayOfWeek)
    {
        var value = Math.Clamp(dayOfWeek ?? (int)DayOfWeek.Monday, 0, 6);
        return (DayOfWeek)value;
    }

    private static DateTime GetMostRecentDailyScheduleUtc(DateTime utcNow, int hourUtc)
    {
        var scheduled = DateTime.SpecifyKind(utcNow.Date.AddHours(hourUtc), DateTimeKind.Utc);
        if (scheduled > utcNow)
        {
            scheduled = scheduled.AddDays(-1);
        }

        return scheduled;
    }

    private static DateTime GetMostRecentWeeklyScheduleUtc(DateTime utcNow, DayOfWeek targetDay, int hourUtc)
    {
        var dayOffset = (int)targetDay - (int)utcNow.DayOfWeek;
        var scheduled = utcNow.Date.AddDays(dayOffset).AddHours(hourUtc);

        if (scheduled > utcNow)
        {
            scheduled = scheduled.AddDays(-7);
        }

        return DateTime.SpecifyKind(scheduled, DateTimeKind.Utc);
    }

    private static DateTime GetMostRecentMonthlyScheduleUtc(DateTime utcNow, int dayOfMonth, int hourUtc)
    {
        var clampedDay = Math.Clamp(dayOfMonth, 1, 31);
        var daysInMonth = DateTime.DaysInMonth(utcNow.Year, utcNow.Month);
        var targetDay = Math.Min(clampedDay, daysInMonth);
        var scheduled = new DateTime(utcNow.Year, utcNow.Month, targetDay, hourUtc, 0, 0, DateTimeKind.Utc);

        if (scheduled > utcNow)
        {
            var previousMonth = utcNow.AddMonths(-1);
            var previousMonthDays = DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month);
            var previousTargetDay = Math.Min(clampedDay, previousMonthDays);
            scheduled = new DateTime(previousMonth.Year, previousMonth.Month, previousTargetDay, hourUtc, 0, 0, DateTimeKind.Utc);
        }

        return scheduled;
    }

    private static DateTime GetMostRecentEveryNDaysScheduleUtc(DateTime utcNow, DateTime anchorDateUtc, int intervalDays, int hourUtc)
    {
        var anchorInstant = DateTime.SpecifyKind(anchorDateUtc.Date.AddHours(hourUtc), DateTimeKind.Utc);
        if (utcNow < anchorInstant)
        {
            // First occurrence has not happened yet.
            return DateTime.MinValue;
        }

        var daysSince = (utcNow.Date - anchorInstant.Date).Days;
        var periodsElapsed = daysSince / intervalDays;
        var scheduled = anchorInstant.AddDays(periodsElapsed * intervalDays);

        if (scheduled > utcNow)
        {
            scheduled = scheduled.AddDays(-intervalDays);
        }

        return scheduled;
    }

    private static bool ParseBool(string? value) => bool.TryParse(value, out var parsed) && parsed;
}
