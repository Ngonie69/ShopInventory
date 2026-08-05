using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShopInventory.Web.Services;

internal static partial class ApiErrorResponse
{
    private const int MaxLogBodyLength = 4000;
    private const int MaxLogIdentifierLength = 64;
    private const string RedactedValue = "[REDACTED]";

    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Key-name fragments whose value is a secret, for the JSON path in <see cref="SanitizeForLog"/>.
    /// </summary>
    /// <remarks>
    /// Matched as substrings and case-insensitively, so <c>refreshToken</c> and <c>X-Api_Key</c> are
    /// both caught. Kept in step with the plain-text <see cref="SensitiveAssignmentRegex"/>, which is
    /// why the hyphen form is spelled out separately — that regex's <c>api[_-]?key</c> already covers
    /// it, and a body should not be redacted differently for arriving as JSON.
    /// </remarks>
    private static readonly string[] SensitiveNameFragments =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "api_key",
        "api-key",
        "apikey",
        "authorization",
        "cookie",
        "session",
        "jwt"
    ];

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

    /// <summary>
    /// Redacts the secrets out of an API response body before it is written to a log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// JSON first, because JSON is the shape the API actually answers with. The plain-text regex
    /// wants the key touching the colon, so the quote in <c>"token":"abc"</c> defeats it and the
    /// value reaches the log whole — and an authentication error body, which is the one most likely
    /// to carry a token, is exactly that shape. Redacting by key name has no such blind spot, and it
    /// reaches into nested objects and arrays that no single regex would.
    /// </para>
    /// <para>
    /// The regex stays on as the fallback, for the bodies that are not JSON at all: a bare string, an
    /// HTML error page, a transport message with a query string in it.
    /// </para>
    /// <para>
    /// This mirrors <c>ShopInventory.Common.Security.SensitiveDataSanitizer</c> rather than calling
    /// it, because ShopInventory.Web does not reference the API project — the same reason the DTOs on
    /// both sides are hand-mirrored. The two are meant to stay alike; change them together.
    /// </para>
    /// <para>
    /// A JSON body comes back reformatted onto one line, because it is reserialized rather than
    /// patched in place. Line breaks inside a string value survive as the escapes they already were,
    /// and the plain-text path leaves its input alone — a non-JSON body is expected to be multi-line
    /// and read as one block. See <see cref="SanitizeIdentifierForLog"/> for why a caller-supplied
    /// identifier is the one thing that does get its line breaks taken out.
    /// </para>
    /// </remarks>
    public static string SanitizeForLog(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return string.Empty;
        }

        var trimmedContent = responseContent.Trim();
        var sanitized = TryRedactJsonBody(trimmedContent) ?? RedactPlainText(trimmedContent);

        return sanitized.Length <= MaxLogBodyLength
            ? sanitized
            : sanitized[..MaxLogBodyLength] + "... [truncated]";
    }

    /// <returns>
    /// The redacted body, or <see langword="null"/> if this content is not JSON and the caller should
    /// fall back to the plain-text path.
    /// </returns>
    private static string? TryRedactJsonBody(string trimmedContent)
    {
        if (!LooksLikeJson(trimmedContent))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(trimmedContent);
            if (node is null)
            {
                return null;
            }

            RedactJsonNode(node);
            return node.ToJsonString(LogJsonOptions);
        }
        catch (JsonException)
        {
            // A body that opens with a brace but does not parse — truncated, or an error page with a
            // stray one in it — is still worth logging, so hand it to the regex instead of dropping
            // it. Returning it unredacted is what we are here to prevent.
            return null;
        }
    }

    private static void RedactJsonNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            // Materialised first: assigning through the indexer mutates the collection being walked.
            foreach (var property in jsonObject.ToList())
            {
                if (IsSensitiveName(property.Key))
                {
                    // Replaces the whole subtree, so {"token":{"value":"..."}} goes with it.
                    jsonObject[property.Key] = RedactedValue;
                }
                else if (property.Value is not null)
                {
                    RedactJsonNode(property.Value);
                }
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                if (item is not null)
                {
                    RedactJsonNode(item);
                }
            }
        }
    }

    private static string RedactPlainText(string value)
    {
        var redacted = SensitiveAssignmentRegex().Replace(value, match =>
        {
            var prefix = match.Groups[1].Value;
            return $"{prefix}{RedactedValue}";
        });

        return BearerTokenRegex().Replace(redacted, $"Bearer {RedactedValue}");
    }

    private static bool IsSensitiveName(string name)
        => SensitiveNameFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Makes a caller-supplied identifier — an account code, a document number, anything that
    /// reached us from a route or a query string — safe to write into a log line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A carriage return or line feed in the value ends the log line early, and everything after it
    /// is read back as a separate entry that the attacker wrote. Structured logging does not save
    /// us here: the placeholder is rendered into the message text by the console and file sinks, so
    /// the newline lands in the output exactly as sent.
    /// </para>
    /// <para>
    /// <see cref="SanitizeForLog"/> is the wrong tool for this — it redacts secrets out of a
    /// response body but deliberately leaves line breaks alone, because a body is expected to be
    /// multi-line and readable. An identifier is neither.
    /// </para>
    /// <para>
    /// Every control character goes, not just the line breaks: U+2028 and U+2029 break lines in
    /// some sinks while <see cref="char.IsControl(char)"/> returns false for them, and a NUL or a
    /// backspace can truncate or rewrite a line in a terminal. The length cap is here because the
    /// route segment carrying this value has no length limit of its own.
    /// </para>
    /// </remarks>
    public static string SanitizeIdentifierForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            // An empty placeholder renders as a gap in the message and reads like a logging bug.
            return "(none)";
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLogIdentifierLength));

        foreach (var character in value)
        {
            if (builder.Length == MaxLogIdentifierLength)
            {
                return builder.Append("... [truncated]").ToString();
            }

            // Written as escapes on purpose: as literals these are invisible in the source, and
            // the next person here cannot tell them apart from each other or from a space.
            var isUnicodeLineBreak = character is '\u2028' or '\u2029';
            builder.Append(char.IsControl(character) || isUnicodeLineBreak ? '?' : character);
        }

        return builder.ToString();
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