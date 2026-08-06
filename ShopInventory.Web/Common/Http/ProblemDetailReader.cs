using System.Text.Json;

namespace ShopInventory.Web.Common.Http;

/// <summary>
/// Reads the message the API actually sent with a failed response.
/// </summary>
/// <remarks>
/// Every report failure — a statement SAP rejected, a timed-out generation, a missing SAP session —
/// reaches the controller as an <c>ErrorType.Failure</c>, which <c>ApiControllerBase.Problem</c>
/// answers as 400 with the real reason in the ProblemDetails title. A caller that checks only the
/// status code discards that body, so the page can only ever say "Failed to load the report" — a
/// sentence that is true of every one of those causes and identifies none of them.
/// </remarks>
public static class ProblemDetailReader
{
    /// <summary>
    /// The reason carried by a ProblemDetails body, or <c>null</c> when the body holds none.
    /// </summary>
    /// <remarks>
    /// Callers decide how to present it: the message alone reads as an explanation, while appending
    /// the status suits a log line or a diagnostic surface.
    /// </remarks>
    public static async Task<string?> ReadMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ReadMessage(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="ReadMessageAsync"/> for a body the caller has already read — a failure is usually
    /// logged verbatim first, and the response stream should not be drained twice to say so.
    /// </summary>
    public static string? ReadMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var title = ReadStringProperty(document.RootElement, "title");
            var detail = ReadStringProperty(document.RootElement, "detail");

            // The title carries the error; the detail is boilerplate on anything the API classifies
            // as a server fault, where it is the only thing that is not "An unexpected error
            // occurred." Prefer whichever is present.
            var message = title ?? detail;
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch (JsonException)
        {
            // A non-ProblemDetails body is still a failure; let the caller report the status
            // rather than masking it behind a parse error.
            return null;
        }
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
