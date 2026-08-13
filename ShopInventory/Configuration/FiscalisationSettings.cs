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
    /// Pins fiscalisation to one device. Leave unset — the default — to use any device the console has.
    /// </summary>
    /// <remarks>
    /// Unset is the setting we want, and it is not merely a fallback. A submission that names no device
    /// makes the platform walk every device it has configured, in order, and fiscalise on the first one
    /// that takes the receipt: a device whose certificate has expired, whose fiscal day will not open,
    /// or that FDMS is refusing simply steps aside for the next. It only ever moves on when it knows
    /// FDMS recorded nothing, so failing over cannot duplicate a receipt, and it will not cross to a
    /// device registered to a different taxpayer. Naming a device here throws all of that away and
    /// fiscalisation stops with it.
    ///
    /// The device that actually took the receipt comes back on the response, so the QR payload and the
    /// serial on the document follow the failover rather than this setting.
    ///
    /// Set it only to force one device deliberately, and expect no failover while it is set. Reads —
    /// the fiscal configuration and day status — treat 0 as "the console's own device" instead, since
    /// there is nothing to fail over on a lookup.
    /// </remarks>
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
    /// SAP tax code to FDMS TaxID, for the pre-SAP path only. Configure in appsettings.
    /// </summary>
    /// <remarks>
    /// Deliberately empty here rather than carrying plausible-looking defaults. FDMS tax ids are
    /// specific to one taxpayer on one FDMS environment — the ids for the ZIMRA test service are not
    /// the ids for the live one — so a default that looks reasonable is a default that is wrong
    /// somewhere. Empty falls through to <see cref="DefaultTaxId"/>, and an unset DefaultTaxId is
    /// rejected by the platform's own validation, which is the loud failure we want.
    ///
    /// The authoritative list is the device's active taxes from /api/fiscal-config. The Fiscalisation
    /// platform keeps the equivalent mapping for its SAP path in SapServiceLayer:TaxMappings; keep the
    /// two in step.
    /// </remarks>
    public Dictionary<string, int> TaxIdMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// FDMS TaxID for lines whose SAP tax code is not in <see cref="TaxIdMappings"/>.
    /// </summary>
    public int DefaultTaxId { get; set; }

    /// <summary>
    /// HS code used when a line carries none. FDMS requires one on every invoice line for a
    /// VAT-registered taxpayer, and it must be 4 or 8 digits. Credit and debit notes are exempt.
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
