using System.Globalization;
using System.Security.Cryptography;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Builds the ZIMRA receipt verification code and QR payload.
/// </summary>
/// <remarks>
/// REVMax returned a ready-made QR code and verification code on every transaction. The Fiscalisation
/// platform returns neither on its API surface — it composes them in its own console and exposes them
/// only through a query behind its login. So we compose them here from the pieces it does return.
///
/// This is a port of the platform's composer
/// (Fiscalisation.Application/Queries/GetFiscalisationDocuments/GetFiscalisationDocumentsQueryHandler.cs,
/// TryCreateReceiptQrData and BuildQrPayload). Keep it byte-identical to that: a payload with the wrong
/// padding or date format still renders as a scannable QR code and still looks right on an invoice, but
/// it resolves to an invalid ZIMRA verification page. Nothing downstream of here would notice.
/// </remarks>
public static class FiscalReceiptQrComposer
{
    /// <summary>
    /// First 16 hex characters of the MD5 digest of the raw device signature bytes.
    /// </summary>
    /// <remarks>
    /// MD5 is not a security choice here — it is what the ZIMRA FDMS specification prescribes for the
    /// QR verification segment, so it cannot be substituted.
    /// </remarks>
    public static string? TryCreateVerificationCode(string? deviceSignatureValue)
    {
        if (string.IsNullOrWhiteSpace(deviceSignatureValue))
        {
            return null;
        }

        try
        {
            var signatureBytes = Convert.FromBase64String(deviceSignatureValue);
            var digest = MD5.HashData(signatureBytes);
            return Convert.ToHexString(digest)[..16];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Composes the QR payload: {qrUrl}/{deviceId:D10}{receiptDate:ddMMyyyy}{receiptGlobalNo:D10}{verificationCode}
    /// </summary>
    public static string? BuildQrPayload(
        string? qrUrl,
        int deviceId,
        DateTime receiptDate,
        int receiptGlobalNo,
        string? verificationCode)
    {
        if (string.IsNullOrWhiteSpace(qrUrl) || string.IsNullOrWhiteSpace(verificationCode))
        {
            return null;
        }

        return string.Concat(
            qrUrl.TrimEnd('/'),
            "/",
            deviceId.ToString("D10", CultureInfo.InvariantCulture),
            receiptDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture),
            receiptGlobalNo.ToString("D10", CultureInfo.InvariantCulture),
            verificationCode);
    }

    /// <summary>
    /// Groups a verification code into blocks of four for display, e.g. "A1B2C3D4E5F60718"
    /// becomes "A1B2-C3D4-E5F6-0718". Display only — never send this form to ZIMRA.
    /// </summary>
    public static string FormatVerificationCode(string verificationCode)
    {
        if (string.IsNullOrWhiteSpace(verificationCode))
        {
            return string.Empty;
        }

        var groups = new List<string>();
        for (var index = 0; index < verificationCode.Length; index += 4)
        {
            groups.Add(verificationCode.Substring(index, Math.Min(4, verificationCode.Length - index)));
        }

        return string.Join("-", groups);
    }

    /// <summary>
    /// Why no QR payload could be built, for display in place of a broken image.
    /// </summary>
    public static string? ResolveUnavailableReason(string? qrUrl, string? verificationCode)
    {
        if (string.IsNullOrWhiteSpace(qrUrl))
        {
            return "The FDMS configuration did not return a QR URL for this device.";
        }

        return string.IsNullOrWhiteSpace(verificationCode)
            ? "The receipt device signature could not be converted into the QR verification segment."
            : null;
    }
}
