using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShopInventory.Common.Security;

public static partial class SensitiveDataSanitizer
{
    private const int DefaultMaxLength = 4000;
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