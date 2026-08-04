namespace ShopInventory.Configuration;

/// <summary>
/// Controls the morning sweep that raises low-stock notifications.
/// </summary>
/// <remarks>
/// The <c>LowStock</c> notification category, its audience rules and
/// <c>INotificationService.CreateLowStockAlertAsync</c> all existed for a long time with nothing
/// calling them, so no low-stock alert was ever raised. This is the trigger.
/// </remarks>
public sealed class LowStockAlertSettings
{
    public const string SectionName = "LowStockAlert";

    /// <summary>
    /// Runs the sweep. It reads the cached per-warehouse stock the reports already use rather than
    /// querying SAP item by item, so the cost is the same as opening the low-stock report once.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When the sweep runs, CAT. Defaults to 07:30 — after the 07:00 daily stock snapshot, so it
    /// measures the figures the day is actually starting from, and before the depots begin loading.
    /// </summary>
    public string ReviewTimeCAT { get; set; } = "07:30";

    /// <summary>
    /// Stock at or below this counts as low. Matches the low-stock report's own default so the
    /// notification and the report agree on what they are counting.
    /// </summary>
    public decimal ReorderThreshold { get; set; } = 10m;

    /// <summary>
    /// Limits the sweep to one warehouse. Null sweeps every active warehouse.
    /// </summary>
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// How many individual critical items get their own notification on top of the summary. The
    /// full list always goes to the log — this exists so a bad morning cannot flood the bell, which
    /// is the failure mode that hid every other module's notifications in the first place.
    /// </summary>
    public int CriticalItemNotificationLimit { get; set; } = 10;
}
