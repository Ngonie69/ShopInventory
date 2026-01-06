namespace ShopInventory.Web.Data;

/// <summary>
/// Application settings stored in the database
/// </summary>
public class AppSetting
{
    public int Id { get; set; }

    /// <summary>
    /// The category of the setting (e.g., "General", "API", "Display")
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// The key/name of the setting
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The value of the setting
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// The data type of the value (string, int, bool, decimal)
    /// </summary>
    public string DataType { get; set; } = "string";

    /// <summary>
    /// A description of what this setting does
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this setting is visible in the UI
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Whether this setting can be modified by users
    /// </summary>
    public bool IsEditable { get; set; } = true;

    /// <summary>
    /// The order in which this setting appears in the UI
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// When the setting was last modified
    /// </summary>
    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who last modified this setting
    /// </summary>
    public string? LastModifiedBy { get; set; }
}

/// <summary>
/// Common setting keys
/// </summary>
public static class SettingKeys
{
    // General settings
    public const string CompanyName = "CompanyName";
    public const string DefaultWarehouse = "DefaultWarehouse";
    public const string DefaultCurrency = "DefaultCurrency";
    public const string DateFormat = "DateFormat";
    public const string TimeFormat = "TimeFormat";

    // API settings
    public const string ApiBaseUrl = "ApiBaseUrl";
    public const string CacheDurationMinutes = "CacheDurationMinutes";
    public const string AutoSyncEnabled = "AutoSyncEnabled";
    public const string SyncIntervalMinutes = "SyncIntervalMinutes";

    // Display settings
    public const string PageSize = "PageSize";
    public const string Theme = "Theme";
    public const string ShowPricesWithTax = "ShowPricesWithTax";

    // Audit settings
    public const string AuditRetentionDays = "AuditRetentionDays";
    public const string AuditEnabled = "AuditEnabled";
}

/// <summary>
/// Setting categories
/// </summary>
public static class SettingCategories
{
    public const string General = "General";
    public const string API = "API";
    public const string Display = "Display";
    public const string Audit = "Audit";
    public const string Security = "Security";
}
