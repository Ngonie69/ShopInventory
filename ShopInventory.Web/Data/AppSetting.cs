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

    // Data cache settings
    public const string CacheDurationMinutes = "CacheDurationMinutes";

    // Display settings
    public const string PageSize = "PageSize";
    public const string ShowPricesWithTax = "ShowPricesWithTax";

    public const string StatementEmailsLastWeeklySentUtc = "StatementEmails.LastWeeklySentUtc";
    public const string StatementEmailsLastMonthlySentUtc = "StatementEmails.LastMonthlySentUtc";

    public const string PodReportEmailsEnabled = "PodReportEmails.Enabled";

    /// <summary>
    /// Master switch for scheduled van sales report emails. Off unless somebody turns it on —
    /// sending mail reaches people who never asked for it, so it is not a default.
    /// </summary>
    public const string VanReportEmailsEnabled = "VanReportEmails.Enabled";

    // Printing settings
    public const string AutoPrintInvoice = "AutoPrintInvoice";

    /// <summary>
    /// Intended copy count. Advisory only: a web page cannot set the copy count on a print job, so
    /// this is surfaced to the operator to enter in the browser's print dialog.
    /// </summary>
    public const string PrintCopies = "PrintCopies";

    // Payment settings
    public const string IncomingPaymentGLAccount = "IncomingPaymentGLAccount";
    public const string DefaultPaymentMethod = "DefaultPaymentMethod";

    // Driver settings
    public const string DriverVisibleBusinessPartners = "DriverVisibleBusinessPartners";
}

/// <summary>
/// Setting keys that were seeded by earlier versions but nothing reads any more. Rows carrying
/// these keys are deleted on startup so the Settings page never offers a control that has no
/// effect. Keys stay named here (rather than being silently dropped) because the POD ones are
/// still read once, by <c>BackfillPodReportEmailSchedulesAsync</c>, before they are pruned.
/// </summary>
public static class RetiredSettingKeys
{
    /// <summary>The API base URL comes from configuration/HttpClient registration, not the DB.</summary>
    public const string ApiBaseUrl = "ApiBaseUrl";

    /// <summary>Nothing schedules a background master-data sync; Data Sync is manual only.</summary>
    public const string AutoSyncEnabled = "AutoSyncEnabled";
    public const string SyncIntervalMinutes = "SyncIntervalMinutes";

    /// <summary>Auditing is unconditional and there is no retention job.</summary>
    public const string AuditEnabled = "AuditEnabled";
    public const string AuditRetentionDays = "AuditRetentionDays";

    /// <summary>Theme is per-browser, owned by the layout's toggle and local storage.</summary>
    public const string Theme = "Theme";

    /// <summary>Never seeded, never read.</summary>
    public const string TimeFormat = "TimeFormat";

    // A web page cannot route a print job to a named printer — the browser's print dialog owns
    // that choice, and printPdfFromBase64 never received a printer name to begin with. The lists
    // these fed were also enumerated on the *server*, so they showed the web host's printers
    // rather than the cashier's.
    public const string PrinterName = "PrinterName";
    public const string SavedPrinters = "SavedPrinters";

    // Only Admin and Cashier reach the incoming payment screen, and both now serve every
    // business partner, so this list filtered nobody.
    public const string AllowedPaymentBusinessPartners = "AllowedPaymentBusinessPartners";

    // Superseded by the PodReportEmailSchedules table, which carries recipients and cadence
    // per schedule.
    public const string PodReportEmailsTo = "PodReportEmails.To";
    public const string PodReportEmailsCc = "PodReportEmails.Cc";
    public const string PodReportEmailsWeeklyDayOfWeek = "PodReportEmails.WeeklyDayOfWeek";
    public const string PodReportEmailsWeeklySendHourUtc = "PodReportEmails.WeeklySendHourUtc";
    public const string PodReportEmailsMonthlyDayOfMonth = "PodReportEmails.MonthlyDayOfMonth";
    public const string PodReportEmailsMonthlySendHourUtc = "PodReportEmails.MonthlySendHourUtc";
    public const string PodReportEmailsLastWeeklySentUtc = "PodReportEmails.LastWeeklySentUtc";
    public const string PodReportEmailsLastMonthlySentUtc = "PodReportEmails.LastMonthlySentUtc";

    public static readonly string[] All =
    {
        ApiBaseUrl,
        AutoSyncEnabled,
        SyncIntervalMinutes,
        AuditEnabled,
        AuditRetentionDays,
        Theme,
        TimeFormat,
        PrinterName,
        SavedPrinters,
        AllowedPaymentBusinessPartners,
        PodReportEmailsTo,
        PodReportEmailsCc,
        PodReportEmailsWeeklyDayOfWeek,
        PodReportEmailsWeeklySendHourUtc,
        PodReportEmailsMonthlyDayOfMonth,
        PodReportEmailsMonthlySendHourUtc,
        PodReportEmailsLastWeeklySentUtc,
        PodReportEmailsLastMonthlySentUtc
    };
}

/// <summary>
/// Setting categories
/// </summary>
public static class SettingCategories
{
    public const string General = "General";
    public const string API = "API";
    public const string Display = "Display";

    /// <summary>Holds only internal, invisible bookkeeping rows (last-sent timestamps).</summary>
    public const string Notifications = "Notifications";

    public const string EmailReports = "Email Reports";
    public const string Printing = "Printing";
    public const string Payments = "Payments";
    public const string DriverAccess = "Driver Access";
}
