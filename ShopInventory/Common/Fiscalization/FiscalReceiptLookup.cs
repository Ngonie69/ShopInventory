using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// What the Fiscalisation platform knows about one document.
/// </summary>
internal sealed record FiscalReceiptSnapshot(
    bool IsFiscalised,
    int? ReceiptGlobalNo,
    string? QrCode,
    string? VerificationCode,
    string? DeviceSerialNumber,
    string? DeviceId,
    string? FiscalDay,
    DateTime TimestampUtc,
    string? RawResponseJson);

/// <summary>
/// Shared read-back against <c>GET /api/receipts/check</c>, used by the invoice and credit-note
/// status syncs.
/// </summary>
internal static class FiscalReceiptLookup
{
    /// <summary>
    /// Looks a document up by its SAP document number.
    /// </summary>
    /// <returns>
    /// A snapshot when the platform answered — which may say the document is not fiscalised. Null when
    /// the lookup itself failed, which is different: "we could not find out" must not be recorded as
    /// "not fiscalised".
    /// </returns>
    public static async Task<FiscalReceiptSnapshot?> TryLookupAsync(
        IFiscalisationApiClient client,
        IFiscalDeviceConfigCache configCache,
        int docNum,
        ReceiptType receiptType,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        CheckFiscalisedReceiptApiResponse response;

        try
        {
            // Device 0 searches every device. A document may have been fiscalised on one other than
            // the device it was submitted to, and anything narrower reports it as missing.
            response = await client.CheckReceiptAsync(
                0,
                docNum.ToString(CultureInfo.InvariantCulture),
                receiptType,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FiscalisationApiException ex) when (
            string.Equals(ex.ErrorCode, FiscalisationApiClient.ApiKeyNotConfiguredErrorCode, StringComparison.Ordinal))
        {
            // Startup already warned, once, that fiscalisation is on with no key. Saying it again per
            // document — 133 times in one production day, each with a stack trace — told nobody anything
            // new and buried the warnings that did.
            logger.LogDebug(
                "Fiscal receipt lookup for {ReceiptType} {DocNum} skipped: no Fiscalisation API key is configured",
                receiptType,
                docNum);

            return null;
        }
        catch (FiscalisationApiException ex)
        {
            // The platform answered and refused. Its status, code and detail are the whole story; the
            // stack underneath is HttpClient's own and the same every time.
            logger.LogWarning(
                "Fiscal receipt lookup failed for {ReceiptType} {DocNum}: HTTP {StatusCode} {ErrorCode}: {Detail}",
                receiptType,
                docNum,
                (int)ex.StatusCode,
                ex.ErrorCode ?? "-",
                ex.Message);

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Fiscal receipt lookup failed for {ReceiptType} {DocNum}",
                receiptType,
                docNum);

            return null;
        }

        var match = response.Matches.FirstOrDefault();

        if (!response.IsFiscalised || match is null)
        {
            return new FiscalReceiptSnapshot(
                IsFiscalised: false,
                ReceiptGlobalNo: null,
                QrCode: null,
                VerificationCode: null,
                DeviceSerialNumber: null,
                DeviceId: null,
                FiscalDay: null,
                TimestampUtc: DateTime.UtcNow,
                RawResponseJson: Serialize(response));
        }

        var config = await configCache.TryGetAsync(match.DeviceId, cancellationToken);
        var verificationCode = FiscalReceiptQrComposer.TryCreateVerificationCode(match.DeviceSignatureValue);

        return new FiscalReceiptSnapshot(
            IsFiscalised: true,
            ReceiptGlobalNo: match.ReceiptGlobalNo > 0 ? match.ReceiptGlobalNo : null,
            QrCode: FiscalReceiptQrComposer.BuildQrPayload(
                config?.QrUrl, match.DeviceId, match.ReceiptDate, match.ReceiptGlobalNo, verificationCode),
            VerificationCode: verificationCode is null
                ? null
                : FiscalReceiptQrComposer.FormatVerificationCode(verificationCode),
            DeviceSerialNumber: config?.DeviceSerialNo,
            DeviceId: match.DeviceId.ToString(CultureInfo.InvariantCulture),
            FiscalDay: match.FiscalDayNo.ToString(CultureInfo.InvariantCulture),
            TimestampUtc: ToUtc(match.ReceiptDate),
            RawResponseJson: Serialize(response));
    }

    private static DateTime ToUtc(DateTime receiptDate)
        => receiptDate.Kind switch
        {
            DateTimeKind.Utc => receiptDate,
            DateTimeKind.Local => receiptDate.ToUniversalTime(),
            _ => DateTime.SpecifyKind(receiptDate, DateTimeKind.Utc)
        };

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}
