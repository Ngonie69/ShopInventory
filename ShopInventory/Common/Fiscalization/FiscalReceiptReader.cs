using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShopInventory.Configuration;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// Asks the fiscal device in force what it holds for one SAP document.
/// </summary>
/// <remarks>
/// The read-back counterpart to <see cref="IFiscalizationService"/>, and it exists for the same
/// reason: which device answers is decided by <c>Fiscalisation:Provider</c>, and every status sync,
/// invoice read and PDF must follow the device that actually filed the receipt. Reading the dormant
/// platform while REVMax does the filing reports every invoice as un-fiscalised.
/// </remarks>
public interface IFiscalReceiptReader
{
    /// <summary>
    /// Looks a document up by its SAP document number.
    /// </summary>
    /// <returns>
    /// A snapshot when the device answered — which may say the document is not fiscalised. Null when
    /// the lookup itself failed, which is different: "we could not find out" must not be recorded as
    /// "not fiscalised".
    /// </returns>
    Task<FiscalReceiptSnapshot?> TryLookupAsync(
        int docNum,
        ReceiptType receiptType,
        ILogger logger,
        CancellationToken cancellationToken);
}

/// <summary>Read-back against the in-house Fiscalisation platform.</summary>
internal sealed class PlatformFiscalReceiptReader : IFiscalReceiptReader
{
    private readonly IFiscalisationApiClient _client;
    private readonly IFiscalDeviceConfigCache _configCache;

    public PlatformFiscalReceiptReader(
        IFiscalisationApiClient client,
        IFiscalDeviceConfigCache configCache)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _configCache = configCache ?? throw new ArgumentNullException(nameof(configCache));
    }

    public Task<FiscalReceiptSnapshot?> TryLookupAsync(
        int docNum,
        ReceiptType receiptType,
        ILogger logger,
        CancellationToken cancellationToken)
        => FiscalReceiptLookup.TryLookupAsync(
            _client, _configCache, docNum, receiptType, logger, cancellationToken);
}

/// <summary>Read-back against the REVMax device.</summary>
/// <remarks>
/// REVMax composes the QR code and verification code itself and returns them on the response, so
/// unlike the platform path there is nothing to derive locally and no device configuration to fetch.
/// </remarks>
internal sealed class RevmaxFiscalReceiptReader : IFiscalReceiptReader
{
    private readonly IRevmaxClient _client;
    private readonly RevmaxSettings _settings;

    public RevmaxFiscalReceiptReader(IRevmaxClient client, RevmaxSettings settings)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<FiscalReceiptSnapshot?> TryLookupAsync(
        int docNum,
        ReceiptType receiptType,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return null;
        }

        InvoiceResponse? response;

        try
        {
            response = await _client.GetInvoiceAsync(
                docNum.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Fiscal receipt lookup failed for {ReceiptType} {DocNum}", receiptType, docNum);
            return null;
        }

        if (response is null)
        {
            // No answer at all is a failed lookup, not an absent receipt.
            return null;
        }

        if (!response.Success || !IsOurReceipt(response, docNum, receiptType, logger))
        {
            // REVMax answered "Invoice not Found", or found a receipt that is not ours to claim —
            // either way our device holds nothing for this document, a real, usable "not fiscalised".
            return NotFiscalised(response);
        }

        var data = response.Data;

        return new FiscalReceiptSnapshot(
            IsFiscalised: true,
            ReceiptGlobalNo: data is not null && data.ReceiptGlobalNo is > 0 and <= int.MaxValue
                ? (int)data.ReceiptGlobalNo
                : null,
            QrCode: NullIfBlank(response.QRcode),
            VerificationCode: NullIfBlank(response.VerificationCode),
            DeviceSerialNumber: NullIfBlank(response.DeviceSerialNumber) ?? NullIfBlank(data?.DeviceSerial),
            DeviceId: NullIfBlank(response.DeviceID),
            FiscalDay: NullIfBlank(response.FiscalDay),
            TimestampUtc: ParseReceiptDate(data?.ReceiptDate),
            RawResponseJson: Serialize(response));
    }

    /// <summary>
    /// Whether the receipt REVMax answered with is our device's, and the kind that was asked about.
    /// </summary>
    /// <remarks>
    /// The read-back half of <c>RevmaxFiscalizationService.IsOurReceipt</c>, and it has to ask the
    /// same question: <c>GetInvoice</c> is not scoped to our device. The box serves several, answers a
    /// number with whichever device's receipt carries it, and reports one serial for all of them, so
    /// <c>DeviceID</c> is the only discriminator. The filing path checked it; this path did not, so the
    /// status backfill and the invoice PDF could adopt another device's receipt as ours. The case that
    /// surfaced it: invoice 776179 (SPA059 USD, 2026-09-17) read "Fiscalised" on receipt 8406 — far
    /// outside our device's ~220000 sequence — with no QR and no verification code, so its PDF printed
    /// no fiscal block.
    ///
    /// Answering "not fiscalised" here matches what the filing path does with the same receipt: it
    /// ignores it and files, and the device's own per-device duplicate check still catches a genuine
    /// repeat.
    /// </remarks>
    private bool IsOurReceipt(
        InvoiceResponse response,
        int docNum,
        ReceiptType receiptType,
        ILogger logger)
    {
        var ourDeviceId = _settings.DefaultRefDeviceId.ToString(CultureInfo.InvariantCulture);

        if (!string.Equals(response.DeviceID?.Trim(), ourDeviceId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "REVMax holds a receipt numbered {DocNum}, but it belongs to device {OwningDeviceId}, "
                    + "not ours ({OurDeviceId}). Reading {ReceiptType} {DocNum} as not fiscalised.",
                docNum,
                response.DeviceID ?? "(none)",
                ourDeviceId,
                receiptType,
                docNum);
            return false;
        }

        var heldType = response.Data?.ReceiptType;

        if (!string.Equals(heldType, receiptType.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "REVMax holds receipt {DocNum} on our device, but it is a {HeldType} where a "
                    + "{ReceiptType} was asked about. Reading it as not fiscalised.",
                docNum,
                heldType ?? "(none)",
                receiptType);
            return false;
        }

        return true;
    }

    private static FiscalReceiptSnapshot NotFiscalised(InvoiceResponse response)
        => new(
            IsFiscalised: false,
            ReceiptGlobalNo: null,
            QrCode: null,
            VerificationCode: null,
            DeviceSerialNumber: null,
            DeviceId: null,
            FiscalDay: null,
            TimestampUtc: DateTime.UtcNow,
            RawResponseJson: Serialize(response));

    /// <summary>
    /// The receipt's own timestamp, falling back to now when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Unspecified kinds are treated as local, not UTC: the device reports its own wall clock, and
    /// stamping a CAT timestamp as UTC would date every receipt two hours early.
    /// </remarks>
    private static DateTime ParseReceiptDate(string? receiptDate)
    {
        if (string.IsNullOrWhiteSpace(receiptDate)
            || !DateTime.TryParse(
                receiptDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return DateTime.UtcNow;
        }

        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}
