namespace ShopInventory.Configuration;

public class SAPSettings
{
    public bool Enabled { get; set; }
    public bool AutoSyncEnabled { get; set; } = true;
    public int SyncIntervalHours { get; set; } = 4;
    public int InitialDelayMinutes { get; set; }
    public string ServiceLayerUrl { get; set; } = string.Empty;
    public string CompanyDB { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Default timeout for standard SAP requests.
    /// </summary>
    public int RequestTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// Extended timeout for bulk sync SAP requests that read large SAP datasets.
    /// </summary>
    public int LongRunningRequestTimeoutMinutes { get; set; } = 20;

    /// <summary>
    /// Maximum time allowed for the temporary SQL-query path used by a single price list.
    /// Price synchronization falls back to the Items API when this budget is exceeded,
    /// preventing a degraded SQL endpoint from occupying an SAP request slot for minutes.
    /// </summary>
    public int PriceListSqlRequestTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Maximum number of attempts for a price-list SQL request before using the Items API fallback.
    /// Keep this low because the fallback provides the same catalog data through a different SAP endpoint.
    /// </summary>
    public int PriceListSqlMaxAttempts { get; set; } = 1;

    /// <summary>
    /// Maximum number of concurrent outbound requests allowed to SAP Service Layer
    /// across the API process.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 6;

    public bool SkipCertificateValidation { get; set; }

    /// <summary>
    /// Optional SAP B1 A/R Invoice numbering series. Configure this when the SAP default
    /// invoice series is tied to a period indicator that does not match the posting date.
    /// </summary>
    public int? InvoiceSeries { get; set; }

    /// <summary>
    /// Optional SAP B1 A/R Invoice numbering series name, for example "New1".
    /// Used only when InvoiceSeries is not configured.
    /// </summary>
    public string? InvoiceSeriesName { get; set; }

    /// <summary>
    /// Optional allowlist of trusted SAP Service Layer certificate thumbprints.
    /// Use this in production when SAP is fronted by a self-signed or privately issued certificate.
    /// </summary>
    public List<string> AllowedServerCertificateThumbprints { get; set; } = new();

    /// <summary>
    /// Whether to use custom UDF fields (U_PackagingCode, U_PackagingCodeLabels, U_PackagingCodeLids).
    /// Set to true for production database, false for test database.
    /// </summary>
    public bool UseCustomFields { get; set; } = true;

    /// <summary>
    /// UNC path to the SAP attachments folder (e.g., \\Kfldb\b1_shf\Paths\Attachments).
    /// POD files are copied here directly, bypassing the SL file upload.
    /// </summary>
    public string AttachmentsPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional path to the same attachments directory as seen by SAP Service Layer.
    /// Use this when the API copies to a Windows UNC/Samba path but Service Layer runs on Linux
    /// and must receive a Linux-local or mounted path in the Attachments2 SourcePath payload.
    /// Leave empty to use AttachmentsPath for both copying and SourcePath.
    /// </summary>
    public string AttachmentsServiceLayerSourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional Windows username used to authenticate to the SAP attachments UNC share.
    /// Leave empty to rely on the IIS app pool identity.
    /// </summary>
    public string AttachmentsUsername { get; set; } = string.Empty;

    /// <summary>
    /// Optional Windows password used to authenticate to the SAP attachments UNC share.
    /// Leave empty to rely on the IIS app pool identity.
    /// </summary>
    public string AttachmentsPassword { get; set; } = string.Empty;

    /// <summary>
    /// Optional Windows domain for the SAP attachments UNC share username.
    /// </summary>
    public string AttachmentsDomain { get; set; } = string.Empty;

    /// <summary>
    /// Number of transient SAP failures before the local circuit opens.
    /// </summary>
    public int CircuitFailureThreshold { get; set; } = 5;

    /// <summary>
    /// How long the local SAP circuit remains open before probing again.
    /// </summary>
    public int CircuitBreakDurationSeconds { get; set; } = 30;
}
