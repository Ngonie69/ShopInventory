namespace ShopInventory.Configuration;

/// <summary>
/// When the day's van sales are posted to SAP. Both times are CAT wall-clock, matching every other
/// daily job in <c>QuartzConfiguration</c>.
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
}
