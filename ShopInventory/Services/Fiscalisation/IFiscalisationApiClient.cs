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
    Task<FiscalConfigApiResponse> GetFiscalConfigAsync(
        int deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live fiscal day status for a device.
    /// </summary>
    Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
        int deviceId,
        CancellationToken cancellationToken = default);
}
