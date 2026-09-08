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
    /// Warehouses to include in daily snapshot. Supplied entirely by <c>DailyStock:MonitoredWarehouses</c>
    /// in appsettings.json — deliberately left empty here.
    /// </summary>
    /// <remarks>
    /// Every warehouse a till sells from has to be listed in configuration. A sale validates against
    /// the day's snapshot, so a missing warehouse reads as zero stock and refuses every line —
    /// silently, until someone tries to sell. <c>KEFBYS</c> (Bulawayo shop) is not <c>KEFBYC</c> (the
    /// Bulawayo depot the vans load from); the two are one character apart and share a site, which is
    /// how the shop stayed off the list.
    ///
    /// Do not give this property a collection initializer. The configuration binder APPENDS to a
    /// collection that already holds items rather than replacing it, so a default here plus the same
    /// list in appsettings.json binds to both — which is exactly what happened: 21 warehouses in each
    /// bound to 42 entries, and the 07:00 snapshot job read SAP twice for every warehouse. Nothing
    /// threw and every value was correct; there were simply twice as many. It surfaced only because a
    /// status page rendered the list and showed KEFBYS twice.
    ///
    /// <c>OptionsCollectionBindingTests</c> pins that binder behaviour and fails if the shape returns.
    /// </remarks>
    public List<string> MonitoredWarehouses { get; set; } = [];

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
