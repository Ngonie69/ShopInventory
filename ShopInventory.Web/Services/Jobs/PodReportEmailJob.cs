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
    /// <summary>
    /// The most recent occurrence of this schedule that has already passed, in local time.
    /// </summary>
    /// <remarks>
    /// The arithmetic moved to <see cref="ReportScheduleCadence"/> when the van reports needed the
    /// same cadence. This stays as the POD-shaped entry point so nothing else in the POD flow had to
    /// change, and so a reader of this job can still see what it asks for.
    /// </remarks>
    internal static DateTime? ComputeMostRecentDueLocal(
        PodReportEmailSchedule schedule,
        PodReportEmailFrequency frequency,
        DateTime nowLocal) =>
        ReportScheduleCadence.MostRecentDueLocal(ToRule(schedule, frequency), nowLocal);

    internal static DateTime ComputeNextDueLocal(
        PodReportEmailSchedule schedule,
        PodReportEmailFrequency frequency,
        DateTime nowLocal) =>
        ReportScheduleCadence.NextDueLocal(ToRule(schedule, frequency), nowLocal);

    /// <summary>
    /// The POD schedule as a cadence rule.
    /// </summary>
    /// <remarks>
    /// Mapped by name rather than by cast: the two enums are declared separately on purpose, and a
    /// cast would keep compiling if either gained a member in the middle. A test asserts the names
    /// still line up.
    /// </remarks>
    private static ReportScheduleCadence.Rule ToRule(
        PodReportEmailSchedule schedule,
        PodReportEmailFrequency frequency) =>
        new(
            Enum.Parse<ReportScheduleFrequency>(frequency.ToString()),
            schedule.SendMinuteOfDay,
            schedule.DayOfWeek,
            schedule.DayOfMonth,
            PodReportEmailService.NormalizeIntervalDays(schedule.IntervalDays),
            schedule.AnchorDateUtc);

    private static bool ParseBool(string? value) => bool.TryParse(value, out var parsed) && parsed;
}
