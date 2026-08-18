using Microsoft.EntityFrameworkCore;
using Quartz;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

/// <summary>
/// Sends any due scheduled van sales report emails.
/// </summary>
/// <remarks>
/// Runs on a one-minute interval so a schedule can be honoured to the minute, and on the clustered
/// scheduler so only one Web node sends — without clustering, every node would mail every recipient
/// on every tick.
///
/// <b>Gated twice, and off by default.</b> The master toggle is an app setting and each schedule has
/// its own flag. Sending mail is the one thing in this suite that reaches people who never asked for
/// it, so it does nothing at all until somebody deliberately turns it on.
///
/// <b>A failure is written to the schedule, not only to the log.</b> A report that has quietly
/// stopped arriving is the failure nobody notices: recipients assume there was nothing to send. The
/// row carries the last error so the settings screen can show it.
///
/// The cadence is <see cref="ReportScheduleCadence"/>, shared with the POD job — so "when is a
/// monthly schedule due" has one answer in this application rather than two that can drift.
/// </remarks>
[DisallowConcurrentExecution]
public sealed class VanReportEmailJob(
    IServiceScopeFactory scopeFactory,
    ILogger<VanReportEmailJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context) => ProcessAsync(context.CancellationToken);

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        if (!ParseBool(await settings.GetValueAsync(SettingKeys.VanReportEmailsEnabled)))
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<WebAppDbContext>();

        var schedules = await db.VanReportEmailSchedules
            .Where(schedule => schedule.Enabled)
            .ToListAsync(cancellationToken);

        if (schedules.Count == 0)
        {
            return;
        }

        var sender = scope.ServiceProvider.GetRequiredService<IVanReportEmailService>();
        var nowLocal = PodScheduleTime.NowLocal();

        foreach (var schedule in schedules)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var rule = ToRule(schedule);

            if (!ReportScheduleCadence.IsDue(rule, nowLocal, schedule.LastSentUtc, schedule.AnchorDateUtc))
            {
                continue;
            }

            await SendAsync(db, sender, schedule, cancellationToken);
        }
    }

    /// <summary>
    /// Sends one schedule and records what happened.
    /// </summary>
    /// <remarks>
    /// <c>LastSentUtc</c> moves only on success. A failed send therefore stays due and is retried on
    /// the next tick, which is right for a transient mail failure — and the error on the row is what
    /// stops a permanently broken schedule retrying in silence.
    /// </remarks>
    private async Task SendAsync(
        WebAppDbContext db,
        IVanReportEmailService sender,
        VanReportEmailSchedule schedule,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await sender.SendAsync(schedule, triggeredBy: "schedule", cancellationToken);

            if (outcome.Success)
            {
                schedule.LastSentUtc = DateTime.UtcNow;
                schedule.LastError = null;

                logger.LogInformation(
                    "Van report email sent. ScheduleId={ScheduleId}, Name={Name}, Recipients={Recipients}",
                    schedule.Id, schedule.Name, outcome.RecipientCount);
            }
            else
            {
                schedule.LastError = Truncate(outcome.Error);

                logger.LogError(
                    "Van report email not sent. ScheduleId={ScheduleId}, Name={Name}, Error={Error}",
                    schedule.Id, schedule.Name, outcome.Error);
            }
        }
        catch (Exception ex)
        {
            // One broken schedule must not stop the others: a van report failing should not take
            // the exception register down with it.
            schedule.LastError = Truncate(ex.Message);

            logger.LogError(
                ex,
                "Van report email threw. ScheduleId={ScheduleId}, Name={Name}",
                schedule.Id, schedule.Name);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    internal static ReportScheduleCadence.Rule ToRule(VanReportEmailSchedule schedule) =>
        new(
            VanReportEmailService.ParseFrequency(schedule.Frequency),
            schedule.SendMinuteOfDay,
            schedule.DayOfWeek,
            schedule.DayOfMonth,
            ReportScheduleCadence.NormalizeIntervalDays(schedule.IntervalDays),
            schedule.AnchorDateUtc);

    private static string? Truncate(string? value) =>
        value is null ? null : value.Length <= 500 ? value : value[..500];

    private static bool ParseBool(string? value) => bool.TryParse(value, out var parsed) && parsed;
}
