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
    /// The SAP user that records approval decisions on A/R credit memo drafts held by SAP's own
    /// approval procedure. Leave empty to decide as <see cref="Username"/>.
    /// </summary>
    /// <remarks>
    /// SAP only accepts a decision from a user listed as an approver on the request's current stage, so
    /// whichever account this names must be an approver on every stage of every approval template that
    /// covers A/R credit memos. The app decides who may click through its own
    /// <c>creditnotes.approve</c> permission and names that person in the decision remarks and the audit
    /// trail; SAP's own record shows this account.
    /// </remarks>
    public string ApprovalApproverUsername { get; set; } = string.Empty;

    /// <summary>
    /// The password for <see cref="ApprovalApproverUsername"/>. Leave empty to use <see cref="Password"/>.
    /// Never logged.
    /// </summary>
    public string ApprovalApproverPassword { get; set; } = string.Empty;

    /// <summary>The SAP user name a credit memo approval decision is recorded as.</summary>
    public string ResolveApprovalApproverUsername()
        => string.IsNullOrWhiteSpace(ApprovalApproverUsername) ? Username : ApprovalApproverUsername.Trim();

    /// <summary>
    /// Whether decisions are recorded as somebody other than the account this app logs into SAP with.
    /// </summary>
    /// <remarks>
    /// The distinction is not cosmetic, and it was measured rather than assumed (KEFALOS_TEST_3,
    /// 2026-09-02). A decision that names no approver is recorded as the session user and needs no
    /// password at all; one that names an approver must carry that approver's password, or SAP
    /// answers <c>400 User code or password is incorrect</c>. So the common case sends no credential
    /// over the wire, and the dedicated case is useless without a password.
    /// </remarks>
    public bool UsesDedicatedApprovalApprover
        => !string.IsNullOrWhiteSpace(ApprovalApproverUsername)
           && !string.Equals(ApprovalApproverUsername.Trim(), Username, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The approver to name on the decision, or null to let SAP record the session user. Null is the
    /// default and the better one: it puts no password on the wire.
    /// </summary>
    public string? ResolveNamedApprovalApprover()
        => UsesDedicatedApprovalApprover ? ApprovalApproverUsername.Trim() : null;

    /// <summary>
    /// The password for <see cref="ResolveNamedApprovalApprover"/>, or null when the session user is
    /// deciding and none is needed. Null alongside a named approver is a configuration fault, not a
    /// request to omit it — see <see cref="UsesDedicatedApprovalApprover"/>.
    /// </summary>
    public string? ResolveApprovalApproverPassword()
        => UsesDedicatedApprovalApprover && !string.IsNullOrWhiteSpace(ApprovalApproverPassword)
            ? ApprovalApproverPassword
            : null;

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
    /// Maximum time allowed for a single stock read against SAP's SQLQueries endpoint.
    /// </summary>
    /// <remarks>
    /// Without a budget of its own a stock read inherits <see cref="RequestTimeoutMinutes"/>, so a
    /// Service Layer that accepts the request and then never answers holds one of
    /// <see cref="MaxConcurrentRequests"/> slots for five minutes. On 2026-09-02 enough of those
    /// piled up that requests a person was waiting on queued 55 seconds for a slot and inventory
    /// transfer submissions ran out the browser's own timeout. A healthy read of this shape answers
    /// in well under a second, so this is well past the point where waiting longer costs more than
    /// giving up and letting the caller degrade.
    /// </remarks>
    public int StockSqlRequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// How long an inventory transfer submission waits for SAP to measure its stock before the
    /// transfer is held for approval anyway.
    /// </summary>
    /// <remarks>
    /// The submission check is advisory — the poster re-runs it authoritatively before anything
    /// reaches SAP — but it used to have no deadline of its own, so a slow Service Layer ran it
    /// until the browser's five-minute timeout fired and took the submission with it. On
    /// 2026-09-02 that lost eleven of thirteen attempts, each after five minutes of waiting. A
    /// healthy validation answers in about three seconds and the slowest that has ever succeeded
    /// took thirty-three, so the default is generous for the check and still a small fraction of
    /// what the client will wait.
    /// </remarks>
    public int TransferStockValidationBudgetSeconds { get; set; } = 45;

    /// <summary>
    /// Maximum number of concurrent outbound requests allowed to SAP Service Layer
    /// across the API process.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 6;

    /// <summary>
    /// How many of <see cref="MaxConcurrentRequests"/> stay out of reach of background work, so
    /// requests a person is waiting on — chiefly sales order approval — are not queued behind
    /// polling and bulk sync traffic. Clamped to leave background work at least one slot.
    /// </summary>
    public int InteractiveReservedRequests { get; set; } = 2;

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

    /// <summary>
    /// The SAP credit card code a shop's card swipe settles against.
    /// </summary>
    /// <remarks>
    /// Null until someone confirms the real code in the company database. Until then a swipe sale is
    /// invoiced but left unsettled, visible as <c>Unmapped</c>, rather than being booked against a
    /// guessed code or quietly folded into the cash till — either of which puts real money in the
    /// wrong account and is only findable by reconciling by hand. Set it and the waiting sales settle
    /// themselves on the next pass.
    /// </remarks>
    public int? SwipeCreditCardCode { get; set; }
}
