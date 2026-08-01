using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

public interface IAppSettingsService
{
    Task<List<AppSetting>> GetAllSettingsAsync();
    Task<List<AppSetting>> GetSettingsByCategoryAsync(string category);
    Task<AppSetting?> GetSettingAsync(string key);
    Task<string?> GetValueAsync(string key);
    Task<T?> GetValueAsync<T>(string key);
    Task SaveSettingAsync(string key, string value, string? modifiedBy = null);
    Task SaveSettingsAsync(Dictionary<string, string> settings, string? modifiedBy = null);
    Task InitializeDefaultSettingsAsync();
}

public class AppSettingsService : IAppSettingsService
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettingsService(IDbContextFactory<WebAppDbContext> dbContextFactory, ILogger<AppSettingsService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<List<AppSetting>> GetAllSettingsAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.AppSettings
            .Where(s => s.IsVisible)
            .OrderBy(s => s.Category)
            .ThenBy(s => s.DisplayOrder)
            .ToListAsync();
    }

    public async Task<List<AppSetting>> GetSettingsByCategoryAsync(string category)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.AppSettings
            .Where(s => s.Category == category && s.IsVisible)
            .OrderBy(s => s.DisplayOrder)
            .ToListAsync();
    }

    public async Task<AppSetting?> GetSettingAsync(string key)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
    }

    public async Task<string?> GetValueAsync(string key)
    {
        var setting = await GetSettingAsync(key);
        return setting?.Value;
    }

    public async Task<T?> GetValueAsync<T>(string key)
    {
        var value = await GetValueAsync(key);
        if (string.IsNullOrEmpty(value))
            return default;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    public async Task SaveSettingAsync(string key, string value, string? modifiedBy = null)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting != null)
        {
            setting.Value = value;
            setting.LastModifiedAt = DateTime.UtcNow;
            setting.LastModifiedBy = modifiedBy;
            await db.SaveChangesAsync();
            _logger.LogInformation("Setting {Key} updated to {Value} by {User}", key, value, modifiedBy);
        }
    }

    public async Task SaveSettingsAsync(Dictionary<string, string> settings, string? modifiedBy = null)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var keys = settings.Keys.ToList();
        var existingSettings = await db.AppSettings
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key);

        foreach (var (key, value) in settings)
        {
            if (existingSettings.TryGetValue(key, out var setting) && setting.IsEditable)
            {
                setting.Value = value;
                setting.LastModifiedAt = DateTime.UtcNow;
                setting.LastModifiedBy = modifiedBy;
            }
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Saved {Count} settings by {User}", settings.Count, modifiedBy);
    }

    public async Task InitializeDefaultSettingsAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var existingKeys = await db.AppSettings.Select(s => s.Key).ToHashSetAsync();

        var defaultSettings = new List<AppSetting>
        {
            // General settings
            new()
            {
                Category = SettingCategories.General,
                Key = SettingKeys.CompanyName,
                Value = "Shop Inventory",
                DataType = "string",
                Description = "Company heading on printed inventory transfer notes (PDFs, reports and email are branded independently)",
                DisplayOrder = 1
            },
            new()
            {
                Category = SettingCategories.General,
                Key = SettingKeys.DefaultWarehouse,
                Value = "01",
                DataType = "string",
                Description = "Warehouse pre-selected in the defaults bar on a new invoice",
                DisplayOrder = 2
            },
            new()
            {
                Category = SettingCategories.General,
                Key = SettingKeys.DefaultCurrency,
                Value = "USD",
                DataType = "string",
                Description = "Currency a new invoice or credit note opens on, until a customer with its own currency is picked (USD or ZIG)",
                DisplayOrder = 3
            },
            new()
            {
                Category = SettingCategories.General,
                Key = SettingKeys.DateFormat,
                Value = "dd MMM yyyy",
                DataType = "string",
                Description = "Format used by the date pickers (e.g., dd/MM/yyyy, MM/dd/yyyy); tables and exports are fixed at dd MMM yyyy",
                DisplayOrder = 4
            },

            // API settings
            new()
            {
                Category = SettingCategories.API,
                Key = SettingKeys.CacheDurationMinutes,
                Value = "30",
                DataType = "int",
                Description = "How long cached SAP master data stays fresh before it is re-read (in minutes)",
                DisplayOrder = 1
            },

            // Display settings
            new()
            {
                Category = SettingCategories.Display,
                Key = SettingKeys.PageSize,
                Value = "20",
                DataType = "int",
                Description = "Number of items per page in lists",
                DisplayOrder = 1
            },
            new()
            {
                Category = SettingCategories.Display,
                Key = SettingKeys.ShowPricesWithTax,
                Value = "false",
                DataType = "bool",
                Description = "Show prices VAT-inclusive on the Create Invoice screen (quotations, sales orders and credit notes always show excluding)",
                DisplayOrder = 2
            },

            // Statement email tracking (internal)
            new()
            {
                Category = SettingCategories.Notifications,
                Key = SettingKeys.StatementEmailsLastWeeklySentUtc,
                Value = "",
                DataType = "string",
                Description = "Last weekly statement sent timestamp (UTC)",
                DisplayOrder = 1,
                IsVisible = false,
                IsEditable = false
            },
            new()
            {
                Category = SettingCategories.Notifications,
                Key = SettingKeys.StatementEmailsLastMonthlySentUtc,
                Value = "",
                DataType = "string",
                Description = "Last monthly statement sent timestamp (UTC)",
                DisplayOrder = 2,
                IsVisible = false,
                IsEditable = false
            },

            // POD report email settings — recipients and cadence live on each row of
            // PodReportEmailSchedules; this is only the master on/off switch.
            new()
            {
                Category = SettingCategories.EmailReports,
                Key = SettingKeys.PodReportEmailsEnabled,
                Value = "false",
                DataType = "bool",
                Description = "Send scheduled POD report emails",
                DisplayOrder = 1
            },

            // Printing settings
            new()
            {
                Category = SettingCategories.Printing,
                Key = SettingKeys.AutoPrintInvoice,
                Value = "false",
                DataType = "bool",
                Description = "Automatically print invoice PDF after successful creation",
                DisplayOrder = 1
            },
            new()
            {
                Category = SettingCategories.Printing,
                Key = SettingKeys.PrintCopies,
                Value = "1",
                DataType = "int",
                Description = "Intended number of copies, shown to the operator in the print dialog",
                DisplayOrder = 2
            },

            // Payment settings
            new()
            {
                Category = SettingCategories.Payments,
                Key = SettingKeys.IncomingPaymentGLAccount,
                Value = "",
                DataType = "string",
                Description = "Default G/L account code used for posting incoming payments",
                DisplayOrder = 1
            },
            new()
            {
                Category = SettingCategories.Payments,
                Key = SettingKeys.DefaultPaymentMethod,
                Value = "cash",
                DataType = "string",
                Description = "Default payment method for incoming payments (cash, transfer, check, credit)",
                DisplayOrder = 3
            },

            // Driver access settings
            new()
            {
                Category = SettingCategories.DriverAccess,
                Key = SettingKeys.DriverVisibleBusinessPartners,
                Value = "[]",
                DataType = "string",
                Description = "JSON array of business partner codes visible to all drivers",
                DisplayOrder = 1,
                IsVisible = false,
                IsEditable = true
            }
        };

        foreach (var setting in defaultSettings)
        {
            if (!existingKeys.Contains(setting.Key))
            {
                db.AppSettings.Add(setting);
            }
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Initialized default application settings");

        // Order matters: the backfill is the last reader of the legacy POD keys, so it has to run
        // before they are pruned.
        await BackfillPodReportEmailSchedulesAsync(db);
        await PruneRetiredSettingsAsync(db);
    }

    /// <summary>
    /// Deletes rows for keys nothing reads any more (see <see cref="RetiredSettingKeys"/>). Without
    /// this, an installation seeded by an earlier version would keep offering controls on the
    /// Settings page that change nothing, because the page renders whatever rows the table holds.
    /// </summary>
    private async Task PruneRetiredSettingsAsync(WebAppDbContext db)
    {
        var removed = await db.AppSettings
            .Where(s => RetiredSettingKeys.All.Contains(s.Key))
            .ExecuteDeleteAsync();

        if (removed > 0)
        {
            _logger.LogInformation("Pruned {Count} retired application setting(s)", removed);
        }
    }

    /// <summary>
    /// One-time migration of the legacy single weekly/monthly POD report email config into the
    /// multi-schedule table. Runs only when no schedules exist yet and a recipient list was configured,
    /// preserving the previously configured recipients and last-sent timestamps.
    /// </summary>
    private async Task BackfillPodReportEmailSchedulesAsync(WebAppDbContext db)
    {
        if (await db.PodReportEmailSchedules.AnyAsync())
        {
            return;
        }

        var podKeys = new[]
        {
            RetiredSettingKeys.PodReportEmailsTo,
            RetiredSettingKeys.PodReportEmailsCc,
            RetiredSettingKeys.PodReportEmailsWeeklyDayOfWeek,
            RetiredSettingKeys.PodReportEmailsWeeklySendHourUtc,
            RetiredSettingKeys.PodReportEmailsMonthlyDayOfMonth,
            RetiredSettingKeys.PodReportEmailsMonthlySendHourUtc,
            RetiredSettingKeys.PodReportEmailsLastWeeklySentUtc,
            RetiredSettingKeys.PodReportEmailsLastMonthlySentUtc
        };

        var values = await db.AppSettings
            .Where(s => podKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var to = GetValueOrEmpty(values, RetiredSettingKeys.PodReportEmailsTo);
        if (string.IsNullOrWhiteSpace(to))
        {
            // Nothing was configured previously; leave the table empty and let the user add schedules.
            return;
        }

        var cc = GetValueOrEmpty(values, RetiredSettingKeys.PodReportEmailsCc);
        var nowUtc = DateTime.UtcNow;

        var weekly = new PodReportEmailSchedule
        {
            Name = "Weekly POD report",
            Enabled = true,
            Frequency = nameof(PodReportEmailFrequency.Weekly),
            DayOfWeek = ParseDayOfWeek(GetValueOrEmpty(values, RetiredSettingKeys.PodReportEmailsWeeklyDayOfWeek)),
            SendMinuteOfDay = LegacyUtcHourToLocalMinuteOfDay(
                GetValueOrEmpty(values, RetiredSettingKeys.PodReportEmailsWeeklySendHourUtc)),
            ToRecipients = to,
            CcRecipients = cc,
            LastSentUtc = ParseUtc(GetValueOrEmpty(values, RetiredSettingKeys.PodReportEmailsLastWeeklySentUtc)),
            AnchorDateUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            CreatedBy = "System (migrated)",
            LastModifiedAtUtc = nowUtc,
            LastModifiedBy = "System (migrated)"
        };

        var monthly = new PodReportEmailSchedule
        {
            Name = "Monthly POD report",
            Enabled = true,
            Frequency = nameof(PodReportEmailFrequency.Monthly),
            DayOfMonth = ParseInt(GetValueOrEmpty(values, RetiredSettingKeys.PodReportEmailsMonthlyDayOfMonth), 1),
            SendMinuteOfDay = LegacyUtcHourToLocalMinuteOfDay(
                GetValueOrEmpty(values, RetiredSettingKeys.PodReportEmailsMonthlySendHourUtc)),
            ToRecipients = to,
            CcRecipients = cc,
            LastSentUtc = ParseUtc(GetValueOrEmpty(values, RetiredSettingKeys.PodReportEmailsLastMonthlySentUtc)),
            AnchorDateUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            CreatedBy = "System (migrated)",
            LastModifiedAtUtc = nowUtc,
            LastModifiedBy = "System (migrated)"
        };

        db.PodReportEmailSchedules.AddRange(weekly, monthly);
        await db.SaveChangesAsync();
        _logger.LogInformation("Back-filled POD report email schedules from legacy configuration (weekly + monthly).");
    }

    private static string GetValueOrEmpty(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    /// <summary>
    /// The legacy settings stored a whole UTC hour; schedules now store a local (CAT) minute-of-day.
    /// </summary>
    private static int LegacyUtcHourToLocalMinuteOfDay(string? utcHourValue)
    {
        var hourUtc = Math.Clamp(ParseInt(utcHourValue, 6), 0, 23);
        var localHour = PodScheduleTime.ToLocal(new DateTime(2000, 1, 1, hourUtc, 0, 0, DateTimeKind.Utc)).Hour;
        return localHour * 60;
    }

    private static int ParseDayOfWeek(string? value) =>
        Enum.TryParse<DayOfWeek>(value, ignoreCase: true, out var parsed) ? (int)parsed : (int)DayOfWeek.Monday;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

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

        return null;
    }
}
