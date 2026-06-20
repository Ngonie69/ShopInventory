using System.Globalization;

namespace ShopInventory.Web.Services;

public sealed class PodReportEmailScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<PodReportEmailScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("POD report email scheduler started.");

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error running POD report email scheduler.");
            }
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var reportEmailService = scope.ServiceProvider.GetRequiredService<IPodReportEmailService>();
        var options = await reportEmailService.GetOptionsAsync();

        if (!options.Enabled || options.To.Count == 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var weeklySchedule = GetMostRecentWeeklyScheduleUtc(
            nowUtc,
            options.WeeklyDayOfWeek,
            options.WeeklySendHourUtc);

        if (!options.LastWeeklySentUtc.HasValue || options.LastWeeklySentUtc.Value < weeklySchedule)
        {
            var (fromDate, toDate) = GetWeeklyPeriod(weeklySchedule);
            await SendScheduledReportAsync(
                reportEmailService,
                PodReportEmailPeriodKind.Weekly,
                weeklySchedule,
                fromDate,
                toDate,
                cancellationToken);
        }

        var monthlySchedule = GetMostRecentMonthlyScheduleUtc(
            nowUtc,
            options.MonthlyDayOfMonth,
            options.MonthlySendHourUtc);

        if (!options.LastMonthlySentUtc.HasValue || options.LastMonthlySentUtc.Value < monthlySchedule)
        {
            var (fromDate, toDate) = GetMonthlyPeriod(monthlySchedule);
            await SendScheduledReportAsync(
                reportEmailService,
                PodReportEmailPeriodKind.Monthly,
                monthlySchedule,
                fromDate,
                toDate,
                cancellationToken);
        }
    }

    private async Task SendScheduledReportAsync(
        IPodReportEmailService reportEmailService,
        PodReportEmailPeriodKind periodKind,
        DateTime scheduledUtc,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var result = await reportEmailService.SendForPeriodAsync(
            periodKind,
            fromDate,
            toDate,
            "System schedule",
            cancellationToken);

        if (!result.Success)
        {
            logger.LogWarning(
                "Scheduled {PeriodKind} POD report email failed for {FromDate} - {ToDate}: {Message}",
                periodKind,
                fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                result.Message);
            return;
        }

        await reportEmailService.MarkScheduledSentAsync(periodKind, scheduledUtc, cancellationToken);
        logger.LogInformation(
            "Scheduled {PeriodKind} POD report email sent for {FromDate} - {ToDate}.",
            periodKind,
            fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static DateTime GetMostRecentWeeklyScheduleUtc(DateTime utcNow, DayOfWeek targetDay, int hourUtc)
    {
        var clampedHour = Math.Clamp(hourUtc, 0, 23);
        var dayOffset = (int)targetDay - (int)utcNow.DayOfWeek;
        var scheduled = utcNow.Date.AddDays(dayOffset).AddHours(clampedHour);

        if (scheduled > utcNow)
        {
            scheduled = scheduled.AddDays(-7);
        }

        return DateTime.SpecifyKind(scheduled, DateTimeKind.Utc);
    }

    private static DateTime GetMostRecentMonthlyScheduleUtc(DateTime utcNow, int dayOfMonth, int hourUtc)
    {
        var clampedDay = Math.Clamp(dayOfMonth, 1, 31);
        var clampedHour = Math.Clamp(hourUtc, 0, 23);
        var daysInMonth = DateTime.DaysInMonth(utcNow.Year, utcNow.Month);
        var targetDay = Math.Min(clampedDay, daysInMonth);
        var scheduled = new DateTime(utcNow.Year, utcNow.Month, targetDay, clampedHour, 0, 0, DateTimeKind.Utc);

        if (scheduled > utcNow)
        {
            var previousMonth = utcNow.AddMonths(-1);
            var previousMonthDays = DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month);
            var previousTargetDay = Math.Min(clampedDay, previousMonthDays);
            scheduled = new DateTime(previousMonth.Year, previousMonth.Month, previousTargetDay, clampedHour, 0, 0, DateTimeKind.Utc);
        }

        return scheduled;
    }

    private static (DateTime fromDate, DateTime toDate) GetWeeklyPeriod(DateTime scheduledUtc)
    {
        var toDate = scheduledUtc.Date.AddDays(-1);
        var fromDate = toDate.AddDays(-6);
        return (fromDate, toDate);
    }

    private static (DateTime fromDate, DateTime toDate) GetMonthlyPeriod(DateTime scheduledUtc)
    {
        var currentMonthStart = new DateTime(scheduledUtc.Year, scheduledUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var fromDate = currentMonthStart.AddMonths(-1);
        var toDate = currentMonthStart.AddDays(-1);
        return (fromDate, toDate);
    }
}
