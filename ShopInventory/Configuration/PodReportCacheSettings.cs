namespace ShopInventory.Configuration;

public sealed class PodReportCacheSettings
{
    public const string SectionName = "PodReportCache";

    /// <summary>
    /// Disabling the cache restores the live-SAP behavior without a deployment rollback.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Credit notes and invoice classifications can change after invoice creation, so snapshots
    /// are intentionally short lived. POD uploads are not subject to this delay because they are
    /// refreshed from PostgreSQL on every cache read.
    /// </summary>
    public int FreshnessMinutes { get; set; } = 15;

    /// <summary>
    /// Expired snapshots remain briefly for diagnostics and are removed opportunistically.
    /// </summary>
    public int RetentionDays { get; set; } = 7;
}
