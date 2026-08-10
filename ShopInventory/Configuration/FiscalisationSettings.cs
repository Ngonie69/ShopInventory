namespace ShopInventory.Configuration;

/// <summary>
/// Configuration for the ZIMRA FDMS Fiscalisation platform that replaced REVMax.
/// </summary>
public class FiscalisationSettings
{
    public const string SectionName = "Fiscalisation";

    /// <summary>
    /// Whether fiscalisation is enabled. When false, fiscalisation is skipped and reported as such
    /// rather than failing the document that triggered it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL for the Fiscalisation console API.
    /// </summary>
    public string BaseUrl { get; set; } = "https://fiscal.kefaloscheese.com/";

    /// <summary>
    /// API key issued from the console's API Keys page. Sent as the X-API-Key header.
    /// Needs the receipt.submit, sap.fiscalise and device.read scopes, and no device allowlist
    /// (a device-scoped key forces an explicit device id on every call, which breaks failover).
    /// Supply via user-secrets locally and the Fiscalisation__ApiKey environment variable in production.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Device to fiscalise on when the caller cannot let the platform choose.
    ///
    /// Only the pre-SAP desktop path needs this. Documents that already exist in SAP are submitted
    /// with device 0, which lets the platform fiscalise on whichever of its devices is healthy.
    /// </summary>
    public int DefaultDeviceId { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// How many times a request rejected *before* it reached FDMS is retried. Only those are retried;
    /// see <see cref="Services.Fiscalisation.FiscalisationApiClient.IsSafeToRetry"/>.
    /// </summary>
    public int TransientRetryCount { get; set; } = 10;

    public int TransientRetryBaseDelayMilliseconds { get; set; } = 500;

    /// <summary>
    /// Default currency code for receipts submitted without one.
    /// </summary>
    public string DefaultCurrency { get; set; } = "USD";

    /// <summary>
    /// SAP tax code to FDMS TaxID. Defaults carry over the mapping REVMax used.
    /// </summary>
    /// <remarks>
    /// These IDs were REVMax's. The new platform validates them against the device's active taxes from
    /// /api/fiscal-config and rejects the receipt if they do not match, so confirm them against a live
    /// ApplicableTaxes list before enabling the pre-SAP path.
    /// </remarks>
    public Dictionary<string, int> TaxIdMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A1"] = 1,
        ["X1"] = 1,
        ["B1"] = 2,
        ["X0"] = 2,
        ["C1"] = 3,
        ["E1"] = 5
    };

    public int DefaultTaxId { get; set; } = 1;

    /// <summary>
    /// HS code used when a line carries none. FDMS requires one on every line for a VAT-registered
    /// taxpayer, and it must be 4 or 8 digits.
    /// </summary>
    public string? DefaultHsCode { get; set; }

    /// <summary>
    /// Prefix applied to a purely numeric pre-SAP invoice number.
    /// </summary>
    /// <remarks>
    /// The platform's idempotency key is (taxpayer, receipt type, invoice number), and invoices posted
    /// from SAP key on their SAP DocNum. A bare numeric external reference therefore shares a namespace
    /// with every present and future DocNum: when SAP later issues that number, the two documents
    /// collide on one key and the second is refused, or worse, silently answered with the first one's
    /// receipt. Prefixing lifts pre-SAP receipts out of that namespace.
    /// </remarks>
    public string PreSapInvoiceNoPrefix { get; set; } = "SI-";
}
