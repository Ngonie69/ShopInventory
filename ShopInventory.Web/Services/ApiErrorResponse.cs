using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShopInventory.Web.Services;

internal static partial class ApiErrorResponse
{
    private const int MaxLogBodyLength = 4000;
    private const string RedactedValue = "[REDACTED]";

    private static readonly HashSet<string> GenericProblemTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unauthorized",
        "Forbidden",
        "Bad Request",
        "Internal Server Error",
        "One or more validation errors occurred.",
        "An error occurred while processing your request."
    };

    public static HttpRequestException CreateHttpRequestException(
        HttpStatusCode statusCode,
        string? responseContent,
        string fallbackMessage,
        string? unauthorizedMessage = null,
        string? forbiddenMessage = null)
    {
        var message = GetFriendlyMessage(
            statusCode,
            responseContent,
            fallbackMessage,
            unauthorizedMessage,
            forbiddenMessage);

        return new HttpRequestException(message, null, statusCode);
    }

    public static string GetFriendlyMessage(
        HttpStatusCode? statusCode,
        string? responseContent,
        string fallbackMessage,
        string? unauthorizedMessage = null,
        string? forbiddenMessage = null)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return unauthorizedMessage ?? "Your session has expired. Please sign in again and try again.";
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            return forbiddenMessage ?? "You do not have permission to perform this action.";
        }

        var structuredMessage = TryExtractStructuredMessage(responseContent);
        if (!string.IsNullOrWhiteSpace(structuredMessage))
        {
            return NormalizeUserMessage(structuredMessage) ?? structuredMessage;
        }

        var plainText = TryExtractPlainText(responseContent);
        if (!string.IsNullOrWhiteSpace(plainText))
        {
            return NormalizeUserMessage(plainText) ?? plainText;
        }

        if (statusCode == HttpStatusCode.Conflict)
        {
            return "This request conflicts with the latest data. Please reload and try again.";
        }

        return fallbackMessage;
    }

    public static string? NormalizeUserMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var candidate = message.Trim();

        var structuredMessage = TryExtractStructuredMessage(candidate);
        if (!string.IsNullOrWhiteSpace(structuredMessage))
        {
            candidate = structuredMessage;
        }
        else
        {
            var plainText = TryExtractPlainText(candidate);
            if (!string.IsNullOrWhiteSpace(plainText))
            {
                candidate = plainText;
            }
        }

        candidate = candidate.ReplaceLineEndings(" ").Trim();
        candidate = StripKnownFailurePrefix(candidate);
        candidate = StripLeadingDelimitedPrefix(candidate);
        candidate = StripLeadingDelimitedPrefix(candidate);
        candidate = candidate.Trim().TrimEnd('.', ';');

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        return EnsureSentenceTerminates(NormalizeSentenceStart(candidate));
    }

    public static string SanitizeForLog(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return string.Empty;
        }

        var sanitized = SensitiveAssignmentRegex().Replace(responseContent.Trim(), match =>
        {
            var prefix = match.Groups[1].Value;
            return $"{prefix}{RedactedValue}";
        });

        sanitized = BearerTokenRegex().Replace(sanitized, $"Bearer {RedactedValue}");

        return sanitized.Length <= MaxLogBodyLength
            ? sanitized
            : sanitized[..MaxLogBodyLength] + "... [truncated]";
    }

    public static string GetFriendlyMessage(
        Exception exception,
        string fallbackMessage,
        string? unauthorizedMessage = null,
        string? forbiddenMessage = null)
    {
        if (exception is HttpRequestException httpRequestException)
        {
            return GetFriendlyMessage(
                httpRequestException.StatusCode,
                httpRequestException.Message,
                fallbackMessage,
                unauthorizedMessage,
                forbiddenMessage);
        }

        if (exception.InnerException is HttpRequestException innerHttpRequestException)
        {
            return GetFriendlyMessage(
                innerHttpRequestException.StatusCode,
                innerHttpRequestException.Message,
                fallbackMessage,
                unauthorizedMessage,
                forbiddenMessage);
        }

        if (exception is TimeoutException or TaskCanceledException
            || exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "The request timed out. Please try again.";
        }

        if (LooksLikeConnectionFailure(exception.Message))
        {
            return "Unable to reach the server right now. Please try again.";
        }

        if (exception is InvalidOperationException)
        {
            var plainText = TryExtractPlainText(exception.Message);
            if (!string.IsNullOrWhiteSpace(plainText))
            {
                return plainText;
            }
        }

        return fallbackMessage;
    }

    private static bool LooksLikeConnectionFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such host", StringComparison.OrdinalIgnoreCase)
            || message.Contains("name or service not known", StringComparison.OrdinalIgnoreCase)
            || message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractStructuredMessage(string? responseContent)
    {
        var jsonPayload = ExtractJsonPayload(responseContent);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonPayload);
            var root = document.RootElement;

            var errorsMessage = TryExtractErrors(root);
            if (!string.IsNullOrWhiteSpace(errorsMessage))
            {
                return errorsMessage;
            }

            if (TryGetString(root, out var detail, "detail", "Detail", "message", "Message"))
            {
                return detail;
            }

            if (TryGetNestedSapMessage(root, out var sapMessage))
            {
                return sapMessage;
            }

            if (TryGetString(root, out var title, "title", "Title")
                && !GenericProblemTitles.Contains(title))
            {
                return title;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? TryExtractErrors(JsonElement root)
    {
        if (!TryGetProperty(root, out var errorsElement, "errors", "Errors"))
        {
            return null;
        }

        var messages = new List<string>();
        CollectMessages(errorsElement, messages);

        return messages.Count == 0
            ? null
            : string.Join("; ", messages.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static void CollectMessages(JsonElement element, List<string> messages)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var stringValue = element.GetString();
                if (!string.IsNullOrWhiteSpace(stringValue))
                {
                    messages.Add(stringValue.Trim());
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectMessages(item, messages);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectMessages(property.Value, messages);
                }
                break;
        }
    }

    private static bool TryGetNestedSapMessage(JsonElement root, out string value)
    {
        value = string.Empty;

        if (!TryGetProperty(root, out var errorElement, "error", "Error")
            || errorElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(errorElement, out var messageElement, "message", "Message"))
        {
            return false;
        }

        if (messageElement.ValueKind == JsonValueKind.String)
        {
            var message = messageElement.GetString();
            if (!string.IsNullOrWhiteSpace(message))
            {
                value = message.Trim();
                return true;
            }
        }

        if (messageElement.ValueKind == JsonValueKind.Object
            && TryGetString(messageElement, out var nestedValue, "value", "Value"))
        {
            value = nestedValue;
            return true;
        }

        return false;
    }

    private static bool TryGetString(JsonElement root, out string value, params string[] propertyNames)
    {
        value = string.Empty;

        if (!TryGetProperty(root, out var element, propertyNames)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var stringValue = element.GetString();
        if (string.IsNullOrWhiteSpace(stringValue))
        {
            return false;
        }

        value = stringValue.Trim();
        return true;
    }

    private static bool TryGetProperty(JsonElement root, out JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out element))
            {
                return true;
            }
        }

        element = default;
        return false;
    }

    private static string? TryExtractPlainText(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return null;
        }

        var candidate = responseContent.Trim();
        if (LooksLikeJson(candidate) || candidate.StartsWith("<", StringComparison.Ordinal))
        {
            return null;
        }

        candidate = StripKnownTransportPrefix(candidate);
        if (string.IsNullOrWhiteSpace(candidate) || LooksLikeGenericTransportMessage(candidate))
        {
            return null;
        }

        return candidate;
    }

    private static string StripKnownTransportPrefix(string value)
    {
        var separatorIndex = value.IndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return value;
        }

        var prefix = value[..separatorIndex];
        if (prefix.StartsWith("Server returned", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("API returned", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("Response status code does not indicate success", StringComparison.OrdinalIgnoreCase))
        {
            return value[(separatorIndex + 2)..].Trim();
        }

        return value;
    }

    private static string StripKnownFailurePrefix(string value)
    {
        var separatorIndex = value.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return value;
        }

        var prefix = value[..separatorIndex].Trim();
        if (prefix.StartsWith("Failed to ", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("Error ", StringComparison.OrdinalIgnoreCase))
        {
            return value[(separatorIndex + 1)..].Trim();
        }

        return value;
    }

    private static string StripLeadingDelimitedPrefix(string value)
    {
        var separatorIndex = value.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return value;
        }

        var prefix = value[..separatorIndex].Trim();
        if (int.TryParse(prefix, out _)
            || IsHttpStatusLabel(prefix))
        {
            return value[(separatorIndex + 3)..].Trim();
        }

        return value;
    }

    private static bool IsHttpStatusLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Enum.TryParse<HttpStatusCode>(value.Replace(" ", string.Empty), true, out _))
        {
            return true;
        }

        return GenericProblemTitles.Contains(value);
    }

    private static string NormalizeSentenceStart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!char.IsLetter(value[0]))
        {
            return value;
        }

        if (value.Length > 1 && char.IsUpper(value[0]) && char.IsUpper(value[1]))
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string EnsureSentenceTerminates(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.EndsWith(".", StringComparison.Ordinal)
            ? value
            : value + ".";
    }

    private static bool LooksLikeGenericTransportMessage(string value)
    {
        return value.StartsWith("Response status code does not indicate success", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("401 (", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("403 (", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("404 (", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("500 (", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractJsonPayload(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return null;
        }

        var trimmedContent = responseContent.Trim();
        if (LooksLikeJson(trimmedContent))
        {
            return trimmedContent;
        }

        var objectStart = trimmedContent.IndexOf('{');
        var arrayStart = trimmedContent.IndexOf('[');
        var jsonStart = objectStart >= 0 && arrayStart >= 0
            ? Math.Min(objectStart, arrayStart)
            : Math.Max(objectStart, arrayStart);

        if (jsonStart < 0)
        {
            return null;
        }

        var candidate = trimmedContent[jsonStart..].Trim();
        return LooksLikeJson(candidate) ? candidate : null;
    }

    private static bool LooksLikeJson(string value)
        => value.StartsWith('{') || value.StartsWith('[');

    [GeneratedRegex("(?i)((?:password|passwd|secret|token|api[_-]?key|authorization|cookie|session|jwt)\\s*[:=]\\s*)([^\\s,;}]+)")]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerTokenRegex();
}