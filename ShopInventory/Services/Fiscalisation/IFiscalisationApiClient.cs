namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Typed client for the ZIMRA FDMS Fiscalisation platform.
/// </summary>
/// <remarks>
/// Every call throws <see cref="FiscalisationApiException"/> on a non-success response rather than
/// returning null, because the error code is what decides whether to retry, reconcile or give up.
/// </remarks>
public interface IFiscalisationApiClient
{
    /// <summary>
    /// Fiscalises a document that already exists in SAP. The platform reads the document's lines,
    /// buyer and totals from SAP itself, so no receipt body is needed.
    /// </summary>
    Task<SubmitReceiptApiResponse> SubmitSapReceiptAsync(
        SapFiscaliseReceiptApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fiscalises a receipt from a full payload. Needed only where no SAP document exists yet.
    /// </summary>
    Task<SubmitReceiptApiResponse> SubmitReceiptAsync(
        SubmitReceiptApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands over a receipt a van handset signed for itself while out of coverage, for the platform to
    /// verify and archive. It reaches ZIMRA when that fiscal day's offline file is uploaded.
    /// </summary>
    /// <remarks>
    /// Only for a device ZIMRA registered in Offline mode, and only for receipts that device signed: the
    /// platform refuses a pre-signed receipt for an Online-mode device, whose sequence FDMS owns. A
    /// device has one chain, so a device's receipts go through this path or through
    /// <see cref="SubmitReceiptAsync"/>, never both.
    ///
    /// Re-sending an already-archived receipt replays the archived one rather than duplicating it, so a
    /// retry after a lost response is safe.
    /// </remarks>
    Task<SubmitReceiptApiResponse> IngestSignedReceiptAsync(
        IngestSignedReceiptApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the platform whether it would accept a receipt, without submitting it.
    /// </summary>
    /// <remarks>
    /// Answers the rules only the platform can answer — whether the invoice number is already archived
    /// on this device, what the next counter and global number are, what FDMS currently says about the
    /// fiscal day — none of which are derivable from the receipt and the cached device configuration
    /// that <see cref="ReceiptPreflight"/> works from.
    ///
    /// Nothing is reserved, locked, numbered or sent to FDMS, so it is safe to call on a receipt that
    /// may never be submitted, and safe to call twice.
    /// </remarks>
    Task<PreflightReceiptApiResponse> PreflightReceiptAsync(
        SubmitReceiptApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same question for a receipt a handset already signed.
    /// </summary>
    /// <remarks>
    /// Worth asking even though nothing can be corrected by then. A receipt that will be refused blocks
    /// every later receipt from its device, so knowing before submission turns an unexplained stopped
    /// van into a named cause.
    /// </remarks>
    Task<PreflightReceiptApiResponse> PreflightSignedReceiptAsync(
        IngestSignedReceiptApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up whether a document was already fiscalised.
    /// </summary>
    /// <remarks>
    /// Pass <paramref name="deviceId"/> 0 to search every device. A document may have been fiscalised on
    /// a device other than the one it was submitted to, so anything narrower can report a fiscalised
    /// document as missing.
    /// </remarks>
    Task<CheckFiscalisedReceiptApiResponse> CheckReceiptAsync(
        int deviceId,
        string invoiceNo,
        ReceiptType receiptType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Device configuration, including the QR base URL needed to compose receipt QR payloads.
    /// </summary>
    /// <remarks>
    /// Pass <paramref name="deviceId"/> 0 for "whichever device the console is set up on" — no device
    /// is named on the wire and the platform answers for its own primary one. Name a device only when
    /// the answer has to be about that device specifically, such as a van handset's own configuration.
    /// </remarks>
    Task<FiscalConfigApiResponse> GetFiscalConfigAsync(
        int deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same lookup, made with a caller-supplied API key instead of the configured one.
    /// </summary>
    /// <remarks>
    /// Only the settings screen needs this. An administrator types a key there and wants to know it
    /// works *before* it is saved, and the configured client cannot answer that: its key is fixed on
    /// the HttpClient's default headers when the client is built, and a saved key is not live until
    /// the app pool recycles anyway.
    ///
    /// Pass null or blank to probe with the configured key, and <paramref name="deviceId"/> 0 to let
    /// the platform answer for its own device.
    /// </remarks>
    Task<FiscalConfigApiResponse> GetFiscalConfigWithApiKeyAsync(
        string? apiKey,
        int deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live fiscal day status for a device, or for the console's own device when
    /// <paramref name="deviceId"/> is 0.
    /// </summary>
    Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
        int deviceId,
        CancellationToken cancellationToken = default);
}
