namespace ShopInventory.Configuration;

/// <summary>
/// Configuration settings for REVMax fiscal integration.
/// </summary>
public class RevmaxSettings
{
    public const string SectionName = "Revmax";

    /// <summary>
    /// Whether REVMax is still in use.
    /// </summary>
    /// <remarks>
    /// Under <see cref="FiscalisationProvider.Revmax"/> this switches fiscalisation itself. Under
    /// <see cref="FiscalisationProvider.Platform"/> REVMax files nothing new. This switch then decides
    /// whether REVMax is still asked about what it filed before the switch, and whether it may still
    /// credit those receipts. Leave it on until nothing REVMax filed is left to credit. Turning it off
    /// is what retires REVMax.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The last local date REVMax filed anything, once the platform has taken over.
    /// </summary>
    /// <remarks>
    /// A SAP document dated after this cannot be on REVMax, so the platform files it without asking
    /// REVMax first. A document dated on or before it is checked against REVMax first, and is held back
    /// if REVMax cannot answer. Null means every document is checked. That is the safe setting until the
    /// cut-over day is known, and it makes a REVMax outage hold back new invoices too.
    ///
    /// This holds only if REVMax really did stop filing on this date. The REVMax vendor's own SAP B1
    /// add-on files invoices straight from the SAP client, so it has to be switched off on every
    /// workstation at the cut-over.
    /// </remarks>
    public DateTime? LastFilingDate { get; set; }

    /// <summary>
    /// Base URL for REVMax API (e.g., http://172.16.16.201:8001)
    /// </summary>
    public string BaseUrl { get; set; } = "http://172.16.16.201:8001";

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Default currency code (e.g., ZWG).
    /// </summary>
    public string DefaultCurrency { get; set; } = "ZWG";

    /// <summary>
    /// Default branch name for transactions.
    /// </summary>
    public string DefaultBranchName { get; set; } = "Kefalos";

    /// <summary>
    /// Default reference device ID.
    /// </summary>
    public int DefaultRefDeviceId { get; set; } = 22862;

    /// <summary>
    /// SAP VAT group code (OVTG.Code) to the REVMax tax id declared on the line.
    /// </summary>
    /// <remarks>
    /// These are REVMax's own tax ids and are NOT the FDMS tax ids in
    /// <c>Fiscalisation:TaxIdMappings</c> — that section belongs to the in-house platform, which talks
    /// to FDMS directly and uses this taxpayer's FDMS ids. REVMax sits in front of
    /// FDMS and maps its own small ids on the way through. Do not copy one section into the other.
    ///
    /// The rate that accompanies the id comes from <c>Tax:RatesByTaxCode</c>, so the rate charged on
    /// the invoice and the rate declared on the receipt cannot drift apart.
    /// </remarks>
    public Dictionary<string, int> TaxIdMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// REVMax tax id for a line whose SAP tax code is not in <see cref="TaxIdMappings"/>.
    /// </summary>
    /// <remarks>
    /// The standard-rated id, matching <c>Tax:VatRate</c> being the fallback rate.
    /// </remarks>
    public int DefaultTaxId { get; set; } = 1;

    /// <summary>
    /// Maximum retry attempts for transient failures.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Retry backoff delays in milliseconds, longest-lived value repeated once exhausted.
    /// </summary>
    /// <remarks>
    /// Empty by default and NOT pre-seeded: the configuration binder appends to a non-empty collection
    /// initializer rather than replacing it, so a default of three plus three in appsettings.json binds
    /// to six. <see cref="EffectiveRetryDelaysMs"/> supplies the fallback instead.
    /// </remarks>
    public int[] RetryDelaysMs { get; set; } = [];

    /// <summary>The configured backoff, or a sane default when none is configured.</summary>
    public int[] EffectiveRetryDelaysMs =>
        RetryDelaysMs.Length > 0 ? RetryDelaysMs : [200, 500, 1000];
}
