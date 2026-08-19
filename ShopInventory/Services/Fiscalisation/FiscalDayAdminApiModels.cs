namespace ShopInventory.Services.Fiscalisation;

// Wire contract for the Fiscalisation platform's admin-authenticated routes, copied field-for-field from
// the platform's FiscalIntegrationEndpoints.ApiModels.cs, .Supplemental.ApiModels.cs and .Auth.cs. Kept in
// a file of its own rather than added to FiscalisationApiModels.cs because these routes sit behind a
// different door: everything here needs a bearer token from POST /auth/token, and nothing here can be
// reached with the X-API-Key the rest of the integration uses.
//
// Same caveat as the API-key contract: there is no shared package, so nothing checks the two repositories
// stay in step when the platform's API changes.

/// <summary>Credentials for <c>POST /auth/token</c>.</summary>
/// <remarks>
/// <c>SetCookie</c> is deliberately absent. The platform refuses the token route outright when it is true —
/// cookie sign-in belongs to the interactive login form — so there is nothing a service account could
/// usefully put in it.
/// </remarks>
public sealed class JwtTokenApiRequest
{
    public string? Username { get; set; }

    public string? Password { get; set; }
}

public sealed class JwtTokenApiResponse
{
    public string AccessToken { get; set; } = string.Empty;

    public string TokenType { get; set; } = "Bearer";

    /// <summary>
    /// When the token stops being accepted. Sent as an unqualified local time by the platform, which is
    /// why <see cref="FiscalDayServiceAccountTokenStore"/> re-reads it as UTC rather than trusting the kind.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}

public sealed class OpenDayApiRequest
{
    public int DeviceId { get; set; }

    /// <summary>
    /// When the day is being opened, in the taxpayer's wall clock. Omitted means "now" at the platform.
    /// </summary>
    public DateTime? FiscalDayOpened { get; set; }

    /// <summary>Omitted lets FDMS assign the next day number, which is what an ordinary open wants.</summary>
    public int? FiscalDayNo { get; set; }
}

public sealed class OpenDayApiResponse
{
    public string OperationID { get; set; } = string.Empty;

    public int FiscalDayNo { get; set; }
}

/// <summary>
/// A close of the active fiscal day at FDMS.
/// </summary>
/// <remarks>
/// Counters and the signature are deliberately never sent. The platform recomputes the day's counters from
/// the receipts it archived and signs them with the device key, and a caller-supplied set would report
/// totals derived from a different population than the one FDMS holds.
///
/// This route is refused for a device ZIMRA registered in Offline mode — see
/// <see cref="FiscalDayLifecycleService"/>, which is why a van handset's day never travels this way.
/// </remarks>
public sealed class CloseDayApiRequest
{
    public int DeviceId { get; set; }

    /// <summary>Omitted lets the platform close whatever day FDMS currently reports as open.</summary>
    public int? FiscalDayNo { get; set; }
}

public sealed class CloseDayApiResponse
{
    public string OperationID { get; set; } = string.Empty;
}

/// <summary>
/// Packages a fiscal day's captured receipts into ready-to-upload FDMS offline file(s).
/// </summary>
/// <remarks>
/// A query on the platform's side, not a command: it reads the archive and builds bytes, reserving nothing
/// and telling FDMS nothing. That is what makes it safe to repeat after a run dies, and it is why the
/// lifecycle does not persist the file bytes locally — it can always ask for them again.
/// </remarks>
public sealed class GenerateOfflineFileApiRequest
{
    public int DeviceId { get; set; }

    public int FiscalDayNo { get; set; }

    /// <summary>
    /// Sequence to stamp on the first file. Omitted derives it from the files FDMS has already accepted for
    /// this day, which is what a resumed run wants — it continues the numbering rather than colliding with it.
    /// </summary>
    public int? FileSequence { get; set; }

    public int? FromReceiptCounter { get; set; }

    public int? ToReceiptCounter { get; set; }

    public DateTime? FiscalDayOpened { get; set; }

    /// <summary>
    /// Re-packages receipts an earlier file already carried. Off, always, on the automated path: it exists
    /// to rebuild a file FDMS rejected, and turning it on by accident sends the same receipts twice.
    /// </summary>
    public bool IncludeAlreadyPackaged { get; set; }

    /// <summary>
    /// Declares the fiscal day closed in the last file generated, carrying the day's counters and their
    /// device signature. This is how an offline device's day is closed — there is no separate close call.
    /// </summary>
    public bool CloseFiscalDay { get; set; }
}

public sealed class GenerateOfflineFileApiResponse
{
    public int DeviceId { get; set; }

    public int FiscalDayNo { get; set; }

    public DateTime FiscalDayOpened { get; set; }

    public List<OfflineFilePackageDto> Files { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

public sealed class OfflineFilePackageDto
{
    public int FileSequence { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>The exact payload to hand to <c>/api/offline-files/submit</c>, byte for byte.</summary>
    public string FileJson { get; set; } = string.Empty;

    public int ReceiptCount { get; set; }

    public int FirstReceiptCounter { get; set; }

    public int LastReceiptCounter { get; set; }

    public int SizeBytes { get; set; }

    /// <summary>True on the one file of the set that also declares the fiscal day closed.</summary>
    public bool DeclaresFiscalDayClose { get; set; }
}

/// <summary>
/// Uploads one offline file to FDMS.
/// </summary>
/// <remarks>
/// Two shapes, and the difference is the whole safety story. Sending <see cref="FileJson"/> uploads;
/// sending <see cref="FiscalDayNo"/> and <see cref="FileSequence"/> with no payload asks the platform to
/// resume the submission it already recorded under that identity, which is how a run whose reply was lost
/// finds out what happened without uploading anything a second time.
/// </remarks>
public sealed class SubmitOfflineFileApiRequest
{
    public int DeviceId { get; set; }

    public string? FileName { get; set; }

    public string? FileJson { get; set; }

    public int? FiscalDayNo { get; set; }

    public int? FileSequence { get; set; }
}

public sealed class SubmitOfflineFileApiResponse
{
    public string OperationID { get; set; } = string.Empty;

    public int FiscalDayNo { get; set; }

    public int ReceiptCount { get; set; }

    /// <summary>How many archived receipts the platform marked as carried by this file.</summary>
    public int StampedReceiptCount { get; set; }

    public bool DeclaredFiscalDayClose { get; set; }

    /// <summary>
    /// The platform's durable state for the file: <c>PendingSubmission</c>, <c>AcceptedPendingReconciliation</c>,
    /// <c>Reconciled</c> or <c>RequiresReconciliation</c>. Only <c>Reconciled</c> means FDMS reported
    /// <c>CompleteSuccessful</c> and every local fact was settled.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    public bool ReconciliationRequired { get; set; }

    public string? ReconciliationDetail { get; set; }
}

public sealed class GetFileStatusApiResponse
{
    /// <summary>Files matching the filter at FDMS, which may exceed the rows returned.</summary>
    public int Total { get; set; }

    public List<FileStatusDto> FileStatus { get; set; } = [];
}

public sealed class FileStatusDto
{
    public string OperationID { get; set; } = string.Empty;

    public DateTime? FileUploadDate { get; set; }

    public int DeviceId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public DateTime? FileProcessingDate { get; set; }

    /// <summary>FDMS's own verdict. <c>CompleteSuccessful</c> is the only one that means the day is with ZIMRA.</summary>
    public string FileProcessingStatus { get; set; } = string.Empty;

    /// <summary>FDMS reports a single processing error per file, not a list.</summary>
    public string? FileProcessingErrorCode { get; set; }

    public int FiscalDayNo { get; set; }

    public DateTime FiscalDayOpenedAt { get; set; }

    public int FileSequence { get; set; }

    /// <summary>True when the file carried a footer, which is to say it declared the fiscal day closed.</summary>
    public bool HasFooter { get; set; }

    public string IpAddress { get; set; } = string.Empty;

    public List<FileInvoiceValidationErrorDto> InvoicesWithValidationErrors { get; set; } = [];
}

public sealed class FileInvoiceValidationErrorDto
{
    public int? ReceiptCounter { get; set; }

    public int? ReceiptGlobalNo { get; set; }

    public List<string> ValidationErrors { get; set; } = [];
}
