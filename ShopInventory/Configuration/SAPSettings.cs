namespace ShopInventory.Configuration;

public class SAPSettings
{
    public bool Enabled { get; set; }
    public string ServiceLayerUrl { get; set; } = string.Empty;
    public string CompanyDB { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool SkipCertificateValidation { get; set; }

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
}
