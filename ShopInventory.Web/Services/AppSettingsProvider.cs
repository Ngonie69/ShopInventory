using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

/// <summary>
/// Singleton service that caches application settings in memory for fast, DB-free reads.
/// Call ReloadAsync() after saving settings to refresh the cache.
/// </summary>
public interface IAppSettingsProvider
{
    string CompanyName { get; }
    string DefaultWarehouse { get; }
    string DefaultCurrency { get; }
    string DateFormat { get; }
    int PageSize { get; }
    int CacheDurationMinutes { get; }
    bool AutoSyncEnabled { get; }
    int SyncIntervalMinutes { get; }
    bool ShowPricesWithTax { get; }
    Task ReloadAsync();
}

public class AppSettingsProvider : IAppSettingsProvider
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly ILogger<AppSettingsProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
#pragma warning disable CS0414
    private bool _loaded;
#pragma warning restore CS0414

    public string CompanyName { get; private set; } = "Shop Inventory";
    public string DefaultWarehouse { get; private set; } = "01";
    public string DefaultCurrency { get; private set; } = "USD";
    public string DateFormat { get; private set; } = "dd MMM yyyy";
    public int PageSize { get; private set; } = 20;
    public int CacheDurationMinutes { get; private set; } = 30;
    public bool AutoSyncEnabled { get; private set; } = true;
    public int SyncIntervalMinutes { get; private set; } = 60;
    public bool ShowPricesWithTax { get; private set; }

    public AppSettingsProvider(IDbContextFactory<WebAppDbContext> dbContextFactory, ILogger<AppSettingsProvider> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task ReloadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var settings = await db.AppSettings.ToDictionaryAsync(s => s.Key, s => s.Value);

            CompanyName = GetString(settings, SettingKeys.CompanyName, "Shop Inventory");
            DefaultWarehouse = GetString(settings, SettingKeys.DefaultWarehouse, "01");
            DefaultCurrency = GetString(settings, SettingKeys.DefaultCurrency, "USD");
            DateFormat = GetString(settings, SettingKeys.DateFormat, "dd MMM yyyy");
            PageSize = GetInt(settings, SettingKeys.PageSize, 20);
            CacheDurationMinutes = GetInt(settings, SettingKeys.CacheDurationMinutes, 30);
            AutoSyncEnabled = GetBool(settings, SettingKeys.AutoSyncEnabled, true);
            SyncIntervalMinutes = GetInt(settings, SettingKeys.SyncIntervalMinutes, 60);
            ShowPricesWithTax = GetBool(settings, SettingKeys.ShowPricesWithTax, false);

            _loaded = true;
            _logger.LogInformation("Application settings loaded: Company={Company}, Warehouse={Warehouse}, Currency={Currency}, DateFormat={DateFormat}",
                CompanyName, DefaultWarehouse, DefaultCurrency, DateFormat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load application settings");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static string GetString(Dictionary<string, string> settings, string key, string defaultValue)
        => settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : defaultValue;

    private static int GetInt(Dictionary<string, string> settings, string key, int defaultValue)
        => settings.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : defaultValue;

    private static bool GetBool(Dictionary<string, string> settings, string key, bool defaultValue)
        => settings.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : defaultValue;
}
