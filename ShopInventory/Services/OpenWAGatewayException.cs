using System.Net;
using System.Text.Json;

namespace ShopInventory.Services;

public sealed class OpenWAGatewayException : Exception
{
    public OpenWAGatewayException(HttpStatusCode statusCode, string? reasonPhrase, string? responseBody)
        : base(BuildMessage(statusCode, reasonPhrase, responseBody))
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ReasonPhrase { get; }

    public string? ResponseBody { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string? reasonPhrase, string? responseBody)
    {
        var parsedMessage = TryExtractMessage(responseBody);
        if (!string.IsNullOrWhiteSpace(parsedMessage))
        {
            return parsedMessage;
        }

        return $"OpenWA request failed with {(int)statusCode} {reasonPhrase}.";
    }

    private static string? TryExtractMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var jsonDocument = JsonDocument.Parse(responseBody);
            var root = jsonDocument.RootElement;

            if (TryReadString(root, "title", out var title))
            {
                return title;
            }

            if (TryReadString(root, "message", out var message))
            {
                return message;
            }

            if (TryReadString(root, "error", out var error))
            {
                return error;
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in errors.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        var firstError = property.Value.EnumerateArray()
                            .FirstOrDefault(item => item.ValueKind == JsonValueKind.String)
                            .GetString();

                        if (!string.IsNullOrWhiteSpace(firstError))
                        {
                            return firstError;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Trim();
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}