using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public class StatementEmailSettings
{
    public bool Enabled { get; set; } = false;
    public DayOfWeek WeeklyDayOfWeek { get; set; } = DayOfWeek.Monday;
    public int WeeklySendHourUtc { get; set; } = 6;
    public int MonthlyDayOfMonth { get; set; } = 1;
    public int MonthlySendHourUtc { get; set; } = 6;
    public bool IncludeClosedInvoices { get; set; } = true;
}

public class StatementEmailScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StatementEmailScheduler> _logger;
    private readonly StatementEmailSettings _settings;

    public StatementEmailScheduler(
        IServiceScopeFactory scopeFactory,
        IOptions<StatementEmailSettings> settings,
        ILogger<StatementEmailScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Statement email scheduler started.");

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running statement email scheduler.");
            }
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;

        using var scope = _scopeFactory.CreateScope();
        var appSettings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        var lastWeeklySent = ParseUtc(await appSettings.GetValueAsync(SettingKeys.StatementEmailsLastWeeklySentUtc));
        var lastMonthlySent = ParseUtc(await appSettings.GetValueAsync(SettingKeys.StatementEmailsLastMonthlySentUtc));

        var weeklySchedule = GetMostRecentWeeklyScheduleUtc(nowUtc, _settings.WeeklyDayOfWeek, _settings.WeeklySendHourUtc);
        if (!lastWeeklySent.HasValue || lastWeeklySent.Value < weeklySchedule)
        {
            var (fromDate, toDate) = GetWeeklyPeriod(weeklySchedule);
            var sentCount = await SendStatementsAsync(scope, "Weekly", fromDate, toDate, cancellationToken);
            await appSettings.SaveSettingAsync(
                SettingKeys.StatementEmailsLastWeeklySentUtc,
                weeklySchedule.ToString("O", CultureInfo.InvariantCulture),
                "System");
            _logger.LogInformation(
                "Weekly statements sent: {Count} for period {FromDate} - {ToDate}.",
                sentCount,
                fromDate.ToString("yyyy-MM-dd"),
                toDate.ToString("yyyy-MM-dd"));
        }

        var monthlySchedule = GetMostRecentMonthlyScheduleUtc(nowUtc, _settings.MonthlyDayOfMonth, _settings.MonthlySendHourUtc);
        if (!lastMonthlySent.HasValue || lastMonthlySent.Value < monthlySchedule)
        {
            var (fromDate, toDate) = GetMonthlyPeriod(monthlySchedule);
            var sentCount = await SendStatementsAsync(scope, "Monthly", fromDate, toDate, cancellationToken);
            await appSettings.SaveSettingAsync(
                SettingKeys.StatementEmailsLastMonthlySentUtc,
                monthlySchedule.ToString("O", CultureInfo.InvariantCulture),
                "System");
            _logger.LogInformation(
                "Monthly statements sent: {Count} for period {FromDate} - {ToDate}.",
                sentCount,
                fromDate.ToString("yyyy-MM-dd"),
                toDate.ToString("yyyy-MM-dd"));
        }
    }

    private async Task<int> SendStatementsAsync(
        IServiceScope scope,
        string frequencyLabel,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<WebAppDbContext>>();
        var statementService = scope.ServiceProvider.GetRequiredService<ICustomerStatementService>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var recipients = await dbContext.CustomerPortalUsers
            .AsNoTracking()
            .Where(user => user.IsActive && user.Status == "Active" && user.ReceiveStatements && user.Email != null && user.Email != "")
            .Select(user => new { user.CardCode, user.CardName, user.Email })
            .ToListAsync(cancellationToken);

        var sentCount = 0;

        foreach (var recipient in recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                continue;
            }

            CustomerStatementResponse statement;
            try
            {
                statement = await statementService.GetStatementAsync(
                    recipient.CardCode,
                    new CustomerStatementRequest
                    {
                        FromDate = fromDate,
                        ToDate = toDate,
                        IncludeClosedInvoices = _settings.IncludeClosedInvoices
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate {Frequency} statement for {CardCode}.", frequencyLabel, recipient.CardCode);
                continue;
            }

            var sent = await emailService.SendStatementEmailAsync(
                recipient.Email,
                recipient.CardName ?? "Customer",
                statement,
                fromDate,
                toDate,
                frequencyLabel);

            if (sent)
            {
                sentCount++;
            }
            else
            {
                _logger.LogWarning(
                    "Failed to send {Frequency} statement email to {Email} ({CardCode}).",
                    frequencyLabel,
                    recipient.Email,
                    recipient.CardCode);
            }
        }

        return sentCount;
    }

    private static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        if (DateTime.TryParse(value, out var fallback))
        {
            return DateTime.SpecifyKind(fallback, DateTimeKind.Utc);
        }

        return null;
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
