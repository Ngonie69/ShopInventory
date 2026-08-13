namespace ShopInventory.Configuration;

/// <summary>
/// How shop till and vending sales are carried to SAP after they have been fiscalised.
/// </summary>
/// <remarks>
/// Tuning only. There is deliberately no on/off switch: the sources this route claims are excluded
/// from the 18:00 consolidation unconditionally, so a way to turn the route off would be a way to
/// leave a day's fiscalised takings claimed by nobody, with nothing logged. The job is registered
/// whenever SAP is enabled.
/// </remarks>
public class DesktopSalePostingSettings
{
    public const string SectionName = "DesktopSalePosting";

    /// <summary>
    /// How often to sweep for sales waiting to post. Every minute keeps SAP within a minute or two of
    /// the till; the cost is a SAP round trip per pass that finds work.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// An end-of-day mop-up, in case a sale spent the day failing.
    /// </summary>
    public string SweepTimeCAT { get; set; } = "20:00";

    public int BatchSize { get; set; } = 25;

    /// <summary>
    /// After this many non-transient failures a sale stops being retried and waits for a human. A
    /// transient failure — SAP down, a timeout — does not count against it, or a short outage would
    /// exhaust the budget for the whole day's takings within minutes.
    /// </summary>
    public int MaxPostingAttempts { get; set; } = 6;

    /// <summary>
    /// How far back a pass looks. More than a day so a sale that failed just before midnight is not
    /// stranded when the date rolls over.
    /// </summary>
    public int LookbackDays { get; set; } = 3;
}
