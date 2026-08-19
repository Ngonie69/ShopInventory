namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// The Fiscalisation platform's fiscal-day and offline-file routes, which need an operator's bearer token
/// rather than the integration's API key.
/// </summary>
/// <remarks>
/// Separate from <see cref="IFiscalisationApiClient"/> because the two speak to different doors on the same
/// host. Everything on that client goes through the platform's API-key group; everything here is on its
/// <c>adminApiGroup</c>, which is <c>RequireAuthorization()</c> — an X-API-Key request reaches it as an
/// anonymous one and is refused. Folding both into one client would mean one <see cref="HttpClient"/>
/// carrying two credentials and a per-call decision about which applies, which is exactly the sort of
/// thing that leaks the wrong one.
///
/// Every call throws <see cref="FiscalisationApiException"/> on a non-success response. Read
/// <see cref="FiscalisationApiException.RequiresReconciliation"/> before doing anything else with a
/// failure: closing a fiscal day twice, or submitting one offline file twice, is not idempotent at FDMS.
///
/// <para><b>That flag is not the whole story, and the gap is the dangerous half.</b> It answers "did the
/// platform tell us the outcome is unknown", which it can only do when it answered at all. A request that
/// times out, or comes back 502/503/504 from something in front of the platform, is the commonest way an
/// outcome becomes unknown and carries no flag whatsoever. A caller of the two irreversible operations —
/// <see cref="CloseFiscalDayAsync"/> and <see cref="SubmitOfflineFileAsync"/> — must treat a transport
/// failure exactly as it treats <c>RequiresReconciliation</c>: the call may have reached FDMS.</para>
/// </remarks>
public interface IFiscalDayAdminApiClient
{
    /// <summary>
    /// Whether any service account has been configured at all. False means every call would 401.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether a credential exists that can act for one device — its own, or the configured default.
    /// </summary>
    /// <remarks>
    /// Asked per device rather than once, because the platform authorises each call against the device the
    /// request names and reads a single <c>FdmsDeviceId</c> claim to do it. A fleet therefore needs one
    /// account per device, and a caller that checks only <see cref="IsConfigured"/> will send N-1 devices
    /// into a bodyless 403 and eventually park their days.
    /// </remarks>
    bool IsConfiguredForDevice(int deviceId);

    /// <summary>Opens a new fiscal day at FDMS for an Online-mode device.</summary>
    /// <remarks>
    /// An Offline-mode device opens its own day locally and declares it in the offline file's header, so
    /// this is never called for a van handset.
    /// </remarks>
    Task<OpenDayApiResponse> OpenFiscalDayAsync(
        OpenDayApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the active fiscal day at FDMS. Refused by the platform for an Offline-mode device, whose day
    /// is closed by the declaration inside its offline file instead.
    /// </summary>
    Task<CloseDayApiResponse> CloseFiscalDayAsync(
        CloseDayApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Packages a day's captured receipts into offline file(s). Reserves nothing and tells FDMS nothing, so
    /// a repeat is safe.
    /// </summary>
    Task<GenerateOfflineFileApiResponse> GenerateOfflineFileAsync(
        GenerateOfflineFileApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads one offline file, or resumes the upload already recorded under its durable identity.
    /// </summary>
    /// <remarks>
    /// The one call here that changes anything at FDMS irreversibly. A failure whose outcome is unknown must
    /// be reconciled through <see cref="GetOfflineFileStatusAsync"/>, never repeated — and that includes a
    /// timeout or a 503, which say nothing about whether the file reached FDMS.
    /// </remarks>
    Task<SubmitOfflineFileApiResponse> SubmitOfflineFileAsync(
        SubmitOfflineFileApiRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// FDMS's processing verdict on the files a device uploaded in a window. This is the read that resolves
    /// an indeterminate submission.
    /// </summary>
    Task<GetFileStatusApiResponse> GetOfflineFileStatusAsync(
        int deviceId,
        DateTime fileUploadedFrom,
        DateTime fileUploadedTill,
        CancellationToken cancellationToken = default);
}
