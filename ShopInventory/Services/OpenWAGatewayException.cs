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

    /// <remarks>
    /// OpenWA is a NestJS app, so a rejected request answers
    /// <c>{"message":["property active should not exist"],"error":"Bad Request","statusCode":400}</c>
    /// - the useful part is an ARRAY under "message", and "error" holds only the status label.
    /// Reading "message" as a string alone therefore skipped it and fell through to "Bad Request",
    /// which is what a caller saw for every validation fault OpenWA raised.
    /// </remarks>
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

            // Ordered most specific first. "error" is last because Nest fills it with the status
            // label, which tells a reader nothing they did not already have from the status code.
            foreach (var propertyName in new[] { "message", "title", "detail", "errors", "error" })
            {
                var collected = new List<string>();
                if (root.TryGetProperty(propertyName, out var property))
                {
                    Collect(property, collected);
                }

                if (collected.Count > 0)
                {
                    return string.Join("; ", collected.Distinct(StringComparer.OrdinalIgnoreCase));
                }
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Trim();
    }

    private static void Collect(JsonElement element, List<string> messages)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    messages.Add(value.Trim());
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, messages);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, messages);
                }

                break;
        }
    }
}