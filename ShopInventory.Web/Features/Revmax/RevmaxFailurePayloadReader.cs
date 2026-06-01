using System.Text;
using System.Text.Json;
using ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;

namespace ShopInventory.Web.Features.Revmax;

internal static class RevmaxFailurePayloadReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static RevmaxTransactExtResponse? ParseResponse(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (TryGetUpstreamResponse(document.RootElement, out var upstreamResponse))
            {
                return upstreamResponse.Deserialize<RevmaxTransactExtResponse>(JsonOptions);
            }
        }
        catch (JsonException)
        {
        }

        try
        {
            return JsonSerializer.Deserialize<RevmaxTransactExtResponse>(rawJson, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

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

            displayMessage = BuildDisplayMessage(invoiceNumber, responseCode, normalizedMessage, responseMessage);
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
            return "REVMax returned an internal error before issuing a receipt.";
        }

        return EnsureSentence(trimmed);
    }

    private static string BuildDisplayMessage(
        string? invoiceNumber,
        string? responseCode,
        string? normalizedMessage,
        string? responseMessage)
    {
        var cleanedMessage = CleanOperatorMessage(normalizedMessage)
            ?? CleanOperatorMessage(responseMessage)
            ?? "REVMax did not return any receipt details for this submission.";

        if (cleanedMessage.StartsWith("REVMax ", StringComparison.OrdinalIgnoreCase))
        {
            return cleanedMessage;
        }

        var prefix = string.IsNullOrWhiteSpace(invoiceNumber)
            ? "REVMax could not complete fiscalisation"
            : $"REVMax could not complete fiscalisation for document {invoiceNumber.Trim()}";

        if (!string.IsNullOrWhiteSpace(responseCode))
        {
            prefix += $" (Code={responseCode.Trim()})";
        }

        return $"{prefix}. {cleanedMessage}";
    }

    private static bool TryGetUpstreamResponse(JsonElement root, out JsonElement upstreamResponse)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("UpstreamResponse", out upstreamResponse)
            && upstreamResponse.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        upstreamResponse = default;
        return false;
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