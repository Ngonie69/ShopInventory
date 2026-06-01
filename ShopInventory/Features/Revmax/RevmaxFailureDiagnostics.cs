using System.Text.Json;
using System.Text.Json.Serialization;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax;

internal static class RevmaxFailureDiagnostics
{
    private const string FailureSource = "REVMaxUpstream";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    internal static string BuildHandledFailureMessage(
        string? invoiceNumber,
        TransactMResponse response,
        string? responseMessage)
    {
        var normalizedInvoice = string.IsNullOrWhiteSpace(invoiceNumber)
            ? "unknown"
            : invoiceNumber.Trim();
        var normalizedCode = string.IsNullOrWhiteSpace(response.Code)
            ? "unknown"
            : response.Code.Trim();
        var normalizedMessage = NormalizeHandledFailureReason(responseMessage);

        return $"REVMax could not complete fiscalisation for document {normalizedInvoice} (Code={normalizedCode}). {normalizedMessage}";
    }

    internal static object BuildHandledFailurePayload(
        string endpointName,
        string? invoiceNumber,
        TransactMResponse response,
        string? originalMessage,
        string normalizedMessage)
    {
        return new
        {
            FailureSource,
            Endpoint = endpointName,
            InvoiceNumber = invoiceNumber,
            ResponseCode = response.Code,
            ResponseMessage = originalMessage,
            NormalizedMessage = normalizedMessage,
            UpstreamResponse = BuildUpstreamResponse(response, originalMessage)
        };
    }

    private static object BuildUpstreamResponse(TransactMResponse response, string? originalMessage)
    {
        return new
        {
            response.Code,
            Message = originalMessage,
            response.QRcode,
            response.VerificationCode,
            response.VerificationLink,
            response.DeviceID,
            response.DeviceSerialNumber,
            response.FiscalDay,
            response.FiscalDayNo,
            response.ReceiptGlobalNo,
            response.ReceiptCounter,
            response.DeviceSerial,
            response.FiscalDayDate,
            response.ReceiptDate,
            response.Data
        };
    }

    private static string NormalizeHandledFailureReason(string? responseMessage)
    {
        if (string.IsNullOrWhiteSpace(responseMessage))
        {
            return "REVMax did not return any receipt details for this submission.";
        }

        var trimmedMessage = CollapseWhitespace(responseMessage.Trim());
        if (LooksLikeInternalError(trimmedMessage))
        {
            return "REVMax returned an internal error before issuing a receipt.";
        }

        return trimmedMessage.EndsWith('.') ? trimmedMessage : $"{trimmedMessage}.";
    }

    private static bool LooksLikeInternalError(string message)
    {
        var normalizedMessage = message.ToLowerInvariant();
        return normalizedMessage.Contains("object reference not set", StringComparison.Ordinal)
            || normalizedMessage.Contains("nullreferenceexception", StringComparison.Ordinal)
            || normalizedMessage.Contains("value cannot be null", StringComparison.Ordinal)
            || normalizedMessage.Contains("index was outside", StringComparison.Ordinal)
            || normalizedMessage.Contains("sequence contains no elements", StringComparison.Ordinal);
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var previousWasWhitespace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }
}