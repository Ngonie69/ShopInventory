using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShopInventory.Common.Security;

public static partial class SensitiveDataSanitizer
{
    private const int DefaultMaxLength = 4000;
    private const int MaxLogIdentifierLength = 64;
    private const string RedactedValue = "[REDACTED]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly string[] SensitiveNameFragments =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "api_key",
        "apikey",
        "authorization",
        "cookie",
        "session",
        "jwt"
    ];

    public static string SanitizeForLog(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = TrySanitizeJson(value) ?? RedactPlainText(value.Trim());
        return Truncate(sanitized, maxLength);
    }

    /// <summary>
    /// Neutralises a short caller-supplied value — a request path, a method, a route identifier —
    /// before it is written into a log message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A carriage return or line feed in the value ends the log line early, and everything after it
    /// is read back as a separate entry that the caller wrote. Structured logging does not save us:
    /// the placeholder is rendered into the message text by the console and file sinks, so the
    /// newline lands in the output exactly as sent. A percent-encoded <c>%0A</c> in a request path
    /// arrives here decoded, so the request line does not have to look suspicious to carry one.
    /// </para>
    /// <para>
    /// <see cref="SanitizeForLog"/> is the wrong tool for this — it redacts secrets out of a
    /// response body but deliberately leaves line breaks alone, because a body is expected to be
    /// multi-line and readable. An identifier is neither.
    /// </para>
    /// <para>
    /// Every control character goes, not just the line breaks: U+2028 and U+2029 break lines in
    /// some sinks while <see cref="char.IsControl(char)"/> returns false for them. The length cap is
    /// here because a request path has no length limit of its own.
    /// </para>
    /// <para>
    /// This mirrors <c>ApiErrorResponse.SanitizeIdentifierForLog</c> on the Web side, which solved
    /// the same problem first. The two are deliberately separate: neither project references the
    /// other, and a shared package for one method is not worth the coupling.
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

            // Written as code points on purpose: as literals these are invisible in the source, and
            // the next person here cannot tell them apart from each other or from a space.
            const char lineSeparator = (char)0x2028;
            const char paragraphSeparator = (char)0x2029;
            var isUnicodeLineBreak = character == lineSeparator || character == paragraphSeparator;
            builder.Append(char.IsControl(character) || isUnicodeLineBreak ? '?' : character);
        }

        return builder.ToString();
    }

    private static string? TrySanitizeJson(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(trimmed);
            if (node is null)
            {
                return null;
            }

            RedactJsonNode(node);
            return node.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RedactJsonNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                if (IsSensitiveName(property.Key))
                {
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

    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "... [truncated]";
    }

    [GeneratedRegex("(?i)((?:password|passwd|secret|token|api[_-]?key|authorization|cookie|session|jwt)\\s*[:=]\\s*)([^\\s,;}]+)")]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerTokenRegex();
}