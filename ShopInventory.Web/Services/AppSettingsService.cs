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

        foreach (var (key, value) in settings)
        {
            var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (setting != null && setting.IsEditable)
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
                Description = "The name of your company",
                DisplayOrder = 1
            },
            new()
            {
                Category = SettingCategories.General,
                Key = SettingKeys.DefaultWarehouse,
                Value = "01",
                DataType = "string",
                Description = "Default warehouse code for new transactions",
                DisplayOrder = 2
            },
            new()
            {
                Category = SettingCategories.General,
                Key = SettingKeys.DefaultCurrency,
                Value = "USD",
                DataType = "string",
                Description = "Default currency for transactions",
                DisplayOrder = 3
            },
            new()
            {
                Category = SettingCategories.General,
                Key = SettingKeys.DateFormat,
                Value = "dd MMM yyyy",
                DataType = "string",
                Description = "Date format for display (e.g., dd/MM/yyyy, MM/dd/yyyy)",
                DisplayOrder = 4
            },

            // API settings
            new()
            {
                Category = SettingCategories.API,
                Key = SettingKeys.ApiBaseUrl,
                Value = "http://localhost:5106",
                DataType = "string",
                Description = "Base URL of the backend API",
                DisplayOrder = 1
            },
            new()
            {
                Category = SettingCategories.API,
                Key = SettingKeys.CacheDurationMinutes,
                Value = "30",
                DataType = "int",
                Description = "How long to cache data before refreshing (in minutes)",
                DisplayOrder = 2
            },
            new()
            {
                Category = SettingCategories.API,
                Key = SettingKeys.AutoSyncEnabled,
                Value = "true",
                DataType = "bool",
                Description = "Automatically sync data with the API",
                DisplayOrder = 3
            },
            new()
            {
                Category = SettingCategories.API,
                Key = SettingKeys.SyncIntervalMinutes,
                Value = "60",
                DataType = "int",
                Description = "Interval between automatic syncs (in minutes)",
                DisplayOrder = 4
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
                Key = SettingKeys.Theme,
                Value = "light",
                DataType = "string",
                Description = "UI theme (light or dark)",
                DisplayOrder = 2
            },
            new()
            {
                Category = SettingCategories.Display,
                Key = SettingKeys.ShowPricesWithTax,
                Value = "false",
                DataType = "bool",
                Description = "Show prices with tax included",
                DisplayOrder = 3
            },

            // Audit settings
            new()
            {
                Category = SettingCategories.Audit,
                Key = SettingKeys.AuditEnabled,
                Value = "true",
                DataType = "bool",
                Description = "Enable audit logging",
                DisplayOrder = 1
            },
            new()
            {
                Category = SettingCategories.Audit,
                Key = SettingKeys.AuditRetentionDays,
                Value = "90",
                DataType = "int",
                Description = "Number of days to retain audit logs",
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
    }
}
