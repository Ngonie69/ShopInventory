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
    /// Looks up whether a document was already fiscalised.
    /// </summary>
    /// <param name="deviceId">
    /// Pass 0 to search every device. A document may have been fiscalised on a device other than the
    /// one it was submitted to, so anything narrower can report a fiscalised document as missing.
    /// </param>
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
