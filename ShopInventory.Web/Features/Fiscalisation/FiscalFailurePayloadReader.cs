using System.Text;
using System.Text.Json;

namespace ShopInventory.Web.Features.Fiscalisation;

/// <summary>
/// Turns a stored fiscal failure payload into something an operator can read.
/// </summary>
/// <remarks>
/// This survives the REVMax decommissioning because the fiscal transaction log still shows historical
/// rows, and those rows hold REVMax-shaped <c>RawResponse</c> JSON. It also understands the new
/// platform's RFC 9457 error body, so both eras render the same way.
/// </remarks>
internal static class FiscalFailurePayloadReader
{
    internal static bool TryReadFailureDetails(
        string? rawJson,
        out string? failureSource,
        out string? endpoint,
        out string? invoiceNumber,
        out string? displayMessage)
    {
        failureSource = null;
        endpoint = null;
        invoiceNumber = null;
        displayMessage = null;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // New platform: an ErrorApiResponse with errorCode/detail/traceId.
            var errorCode = ReadString(root, "errorCode");
            var detail = ReadString(root, "detail");
            if (!string.IsNullOrWhiteSpace(errorCode) || !string.IsNullOrWhiteSpace(detail))
            {
                failureSource = "Fiscalisation";
                endpoint = ReadString(root, "type");
                displayMessage = BuildPlatformMessage(errorCode, detail, ReadString(root, "traceId"));
                return true;
            }

            // Legacy REVMax diagnostics payload.
            failureSource = ReadString(root, "FailureSource");
            endpoint = ReadString(root, "Endpoint");
            invoiceNumber = ReadString(root, "InvoiceNumber");
            var responseCode = ReadString(root, "ResponseCode");
            var normalizedMessage = ReadString(root, "NormalizedMessage");
            var responseMessage = ReadString(root, "ResponseMessage");

            if (string.IsNullOrWhiteSpace(failureSource)
                && string.IsNullOrWhiteSpace(endpoint)
                && string.IsNullOrWhiteSpace(normalizedMessage)
                && string.IsNullOrWhiteSpace(responseMessage))
            {
                return false;
            }

            displayMessage = BuildLegacyDisplayMessage(invoiceNumber, responseCode, normalizedMessage, responseMessage);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string? CleanOperatorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();
        var rawResponseIndex = trimmed.IndexOf("Raw response:", StringComparison.OrdinalIgnoreCase);
        if (rawResponseIndex >= 0)
        {
            trimmed = trimmed[..rawResponseIndex].Trim();
        }

        // Historical messages were prefixed with the REVMax operation name.
        if (trimmed.StartsWith("TransactMExt:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["TransactMExt:".Length..].Trim();
        }
        else if (trimmed.StartsWith("TransactM:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["TransactM:".Length..].Trim();
        }

        trimmed = CollapseWhitespace(trimmed);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (LooksLikeInternalError(trimmed))
        {
            return "The fiscal service returned an internal error before issuing a receipt.";
        }

        return EnsureSentence(trimmed);
    }

    private static string BuildPlatformMessage(string? errorCode, string? detail, string? traceId)
    {
        var message = CleanOperatorMessage(detail)
            ?? "The fiscalisation platform rejected the submission without explaining why.";

        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            message = $"{message} (Code={errorCode.Trim()})";
        }

        // The scrubbed detail is deliberately vague for infrastructure failures; the trace id is how
        // the real message is found in the platform's log.
        return string.IsNullOrWhiteSpace(traceId)
            ? message
            : $"{message} Trace {traceId.Trim()}.";
    }

    private static string BuildLegacyDisplayMessage(
        string? invoiceNumber,
        string? responseCode,
        string? normalizedMessage,
        string? responseMessage)
    {
        var cleanedMessage = CleanOperatorMessage(normalizedMessage)
            ?? CleanOperatorMessage(responseMessage)
            ?? "The fiscal service did not return any receipt details for this submission.";

        var prefix = string.IsNullOrWhiteSpace(invoiceNumber)
            ? "Fiscalisation could not be completed"
            : $"Fiscalisation could not be completed for document {invoiceNumber.Trim()}";

        if (!string.IsNullOrWhiteSpace(responseCode))
        {
            prefix += $" (Code={responseCode.Trim()})";
        }

        return $"{prefix}. {cleanedMessage}";
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
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
        var builder = new StringBuilder(value.Length);
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

    private static string EnsureSentence(string value)
        => value.EndsWith('.') ? value : $"{value}.";
}
