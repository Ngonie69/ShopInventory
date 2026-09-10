using ShopInventory.Models.Revmax;

namespace ShopInventory.Services;

/// <summary>
/// Typed HTTP client interface for REVMax fiscal API.
/// Handles HTTP communication only - validation is done in controller.
/// </summary>
public interface IRevmaxClient
{
    /// <summary>
    /// Gets card/device details from REVMax.
    /// GET /api/RevmaxAPI/GetCardDetails
    /// </summary>
    Task<CardDetailsResponse?> GetCardDetailsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current fiscal day status.
    /// GET /api/RevmaxAPI/GetDayStatus
    /// </summary>
    Task<DayStatusResponse?> GetDayStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current license information.
    /// GET /api/RevmaxAPI/GetLicense
    /// </summary>
    Task<LicenseResponse?> GetLicenseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a new license.
    /// GET /api/RevmaxAPI/SetLicense?license={license}
    /// </summary>
    Task<LicenseResponse?> SetLicenseAsync(string license, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates Z-Report (end of day report).
    /// GET /api/RevmaxAPI/ZReport
    /// </summary>
    Task<ZReportResponse?> GetZReportAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets invoice details by invoice number.
    /// GET /api/RevmaxAPI/GetInvoice/{invoiceNumber}
    /// </summary>
    Task<InvoiceResponse?> GetInvoiceAsync(string invoiceNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets summary of unprocessed invoices.
    /// GET /api/RevmaxAPI/GetUnProcessedInvoicesSummary
    /// </summary>
    /// <remarks>
    /// One of <paramref name="fiscalDayNumber"/> or <paramref name="fiscalDate"/> is required by the
    /// device, despite Swagger marking both optional.
    /// </remarks>
    Task<UnprocessedInvoicesSummaryResponse?> GetUnprocessedInvoicesSummaryAsync(
        string? fiscalDayNumber = null,
        string? fiscalDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a fiscal transaction (invoice or credit note).
    /// POST /api/RevmaxAPI/TransactM
    /// </summary>
    Task<TransactMResponse?> TransactMAsync(TransactMRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a fiscal transaction with extended reference fields.
    /// POST /api/RevmaxAPI/TransactMExt
    /// </summary>
    Task<TransactMExtResponse?> TransactMExtAsync(TransactMExtRequest request, CancellationToken cancellationToken = default);
}
