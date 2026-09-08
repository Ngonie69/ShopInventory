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
    /// How often to sign the sales that were stored unsigned — vending, which prints nothing. Faster
    /// than the posting interval because nothing can post until it has fiscalised, so this delay is
    /// added to that one.
    /// </summary>
    public int FiscalisationIntervalSeconds { get; set; } = 30;

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
    /// After this many attempts a vending sale stops being offered to the fiscalisation platform and
    /// waits for a human. Separate from <see cref="MaxPostingAttempts"/> because the two failures are
    /// different: a SAP post can be retried freely, while every fiscalisation attempt is a submission
    /// to ZIMRA.
    /// </summary>
    public int MaxFiscalisationAttempts { get; set; } = 5;

    /// <summary>
    /// How far back a pass looks. More than a day so a sale that failed just before midnight is not
    /// stranded when the date rolls over.
    /// </summary>
    public int LookbackDays { get; set; } = 3;

    /// <summary>
    /// How long after a post was issued the system waits before it will send that sale again,
    /// when SAP shows no invoice for it.
    /// </summary>
    /// <remarks>
    /// The duplicate guard is a read, and a read can miss a write SAP has committed but not yet made
    /// visible to a filter on a UDF. Five sales were invoiced twice on KEFALOS_USD_NEW2 between 25
    /// August and 8 September 2026 that way, each pair between nought and three minutes apart.
    ///
    /// <para>
    /// So the lookup's "no invoice" is only trustworthy once enough time has passed for that lag to
    /// have cleared. Fifteen minutes is several times the largest gap observed, and the cost of
    /// waiting is a sale posting a quarter of an hour later than it might have — against a duplicate
    /// fiscal receipt, which cannot be withdrawn from ZIMRA and needs a manual credit note.
    /// </para>
    ///
    /// <para>Set to zero to restore the old behaviour of reposting immediately.</para>
    /// </remarks>
    public int UnresolvedPostGraceMinutes { get; set; } = 15;
}
