namespace ShopInventory.Configuration;

/// <summary>
/// When van sales are posted to SAP: a run through the day, and two fixed times to settle it. The
/// times are CAT wall-clock, matching every other daily job in <c>QuartzConfiguration</c>.
/// </summary>
public sealed class VanSalesPostingSettings
{
    public const string SectionName = "VanSalesPosting";

    /// <summary>
    /// Off by default. A deployment that has not yet moved its vans to offline capture has no sales in
    /// this table, and a job that posts nothing every evening is noise rather than safety.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The main run, once the vans are back and their backlogs uploaded.</summary>
    public string PostingTimeCAT { get; set; } = "18:00";

    /// <summary>
    /// The mop-up. A van still out of coverage at 18:00 uploads afterwards, and without a second pass
    /// those sales would wait a full day. Safe to run over already-posted sales: each posts under its own
    /// <c>U_Van_saleorder</c>, which SAP is asked for first.
    /// </summary>
    public string MopUpTimeCAT { get; set; } = "19:30";

    /// <summary>
    /// How often to post held sales during the day. Zero or less runs only at the two times above.
    /// </summary>
    /// <remarks>
    /// Added when van sales moved to fiscalising on the handset before posting. Before that, a sale made
    /// with signal went straight to SAP on the request and only a van out of coverage had anything
    /// waiting, so posting in the evening was enough. Now every van sale is held, and waiting until 18:00
    /// would mean SAP — and everyone reading stock or revenue from it — learning nothing about a trading
    /// day until it was over.
    ///
    /// It also keeps SAP's warehouse figure close to the van's. The handset has to model how far ahead of
    /// SAP it has run (see the van stock ledger, which subtracts exactly that), and the shorter this
    /// interval, the smaller the gap it is modelling and the less there is to get wrong.
    ///
    /// Safe at any frequency: each sale posts under its own <c>U_Van_saleorder</c>, which SAP is asked
    /// for first, so a run that overlaps a previous one adopts rather than duplicates.
    /// </remarks>
    public int IntervalMinutes { get; set; } = 30;
}
