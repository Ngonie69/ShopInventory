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
    ///
    /// Leaving it empty costs the safety net a default provided: configuration is now the only source,
    /// so a missing or misspelled key binds to nothing, and every consumer would read that as a
    /// legitimate answer — snapshotting no warehouses, reporting success, and leaving every till
    /// refusing every sale. <c>DailyStockSettingsValidation</c> refuses an empty list at startup for
    /// that reason.
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

    /// <summary>
    /// Whether the hourly comparison against SAP may correct the ledger as well as report on it.
    /// </summary>
    /// <remarks>
    /// The kill switch for the only thing in this system that writes to the table every till sells
    /// from without a document behind it. Defaults to on because reporting alone was the problem —
    /// the comparison recorded 89 divergences across 93 moved items on 2026-09-11 and corrected none
    /// of them — but a correction going wrong is worse than a divergence going unfixed, so turning it
    /// off has to be one setting and no deploy. With it off the job behaves exactly as it used to.
    /// </remarks>
    public bool ReconcileLedgerAgainstSap { get; set; } = true;

    /// <summary>
    /// Warehouses the hourly comparison may correct. Supplied entirely by
    /// <c>DailyStock:ReconcileWarehouses</c> in appsettings.json — deliberately left empty here, for
    /// the reason spelled out on <see cref="MonitoredWarehouses"/>.
    /// </summary>
    /// <remarks>
    /// A subset of <see cref="MonitoredWarehouses"/> rather than all of it, because the two kinds of
    /// warehouse on that list mean different things by <c>AvailableQuantity</c>.
    ///
    /// <para>
    /// A shop's figure is what the till may still sell, and correcting it is the point. A van's is
    /// the morning load, against which the day's sales are reconciled after the fact — van sales only
    /// reach the ledger at end of day, so during trading a van's row is <i>meant</i> to sit at its
    /// morning figure while SAP moves beneath it. Correcting that would not fix a divergence, it
    /// would destroy the number the van reconciliation is computed from. The vans are the bulk of the
    /// divergences the job reports and none of them are faults.
    /// </para>
    ///
    /// <para>
    /// Empty means nothing is corrected, which is a safe answer but a silent one, so the job says so
    /// plainly when reconciliation is enabled and this is empty rather than logging a quiet success.
    /// </para>
    /// </remarks>
    public List<string> ReconcileWarehouses { get; set; } = [];

    /// <summary>
    /// Whether the hourly comparison also looks for stock that arrived in a warehouse the snapshot has
    /// no row for at all.
    /// </summary>
    /// <remarks>
    /// The comparison itself can only ask about rows that moved, so an item the warehouse held none of
    /// at 07:00 is invisible to it — there is nothing of it to have moved. That is the shape of "the
    /// stock is on the shelf and the till will not put it on a receipt" that survives everything else:
    /// a transfer brings its own row in through the listener, but a goods receipt or a production
    /// receipt booked straight into SAP does not, and the item stays off every till until the next
    /// morning.
    ///
    /// <para>
    /// Finding them costs a whole-warehouse read, which is the scan the rest of this job exists to
    /// avoid, so it is done for a few warehouses per pass in rotation rather than all of them every
    /// time. See <see cref="WarehousesPerDiscoveryPass"/>.
    /// </para>
    /// </remarks>
    public bool DiscoverNewStockArrivals { get; set; } = true;

    /// <summary>
    /// How many warehouses the arrival search covers per hourly pass, in rotation.
    /// </summary>
    /// <remarks>
    /// The throttle on the only expensive thing here. Each warehouse costs the same pair of
    /// whole-warehouse reads the morning fetch makes, against six process-wide SAP slots that a stock
    /// read is exactly what fills. Two per pass covers the seven shop warehouses roughly every three
    /// and a half hours at four reads an hour — against the forty-two the morning fetch makes in one
    /// go. Raise it if the slot pool turns out to have room; the cost is linear and immediate.
    /// </remarks>
    public int WarehousesPerDiscoveryPass { get; set; } = 2;
}
