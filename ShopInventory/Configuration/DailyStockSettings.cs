namespace ShopInventory.Configuration;

/// <summary>
/// Configuration for daily stock snapshot and end-of-day consolidation.
/// </summary>
public class DailyStockSettings
{
    public const string SectionName = "DailyStock";

    /// <summary>
    /// Time (CAT) to fetch daily stock from SAP. Default: 07:00.
    /// </summary>
    public string StockFetchTimeCAT { get; set; } = "07:00";

    /// <summary>
    /// Time (CAT) to run end-of-day consolidation. Default: 18:00.
    /// </summary>
    public string EndOfDayTimeCAT { get; set; } = "18:00";

    /// <summary>
    /// Warehouses to include in daily snapshot.
    /// </summary>
    public List<string> MonitoredWarehouses { get; set; } = new()
    {
        "KEFSHOP", "CORMACH", "CORMACH2", "KEFGRS", "KEFGRC",
        "VAN001", "VAN004", "VAN005", "VAN006", "VAN013",
        "VAN014", "VAN015", "VAN016", "VAN018",
        "KEFBYC", "VAN008", "VAN009", "VAN010", "VAN011"
    };

    /// <summary>
    /// Email addresses to receive the end-of-day report.
    /// </summary>
    public List<string> ReportRecipients { get; set; } = new();

    /// <summary>
    /// Whether to automatically run end-of-day consolidation at the configured time.
    /// </summary>
    public bool EnableAutoConsolidation { get; set; } = true;

    /// <summary>
    /// Whether to automatically run the morning stock fetch at the configured time.
    /// </summary>
    public bool EnableAutoStockFetch { get; set; } = true;

    /// <summary>
    /// Max number of consolidation groups to process per batch.
    /// </summary>
    public int ConsolidationBatchSize { get; set; } = 10;
}
