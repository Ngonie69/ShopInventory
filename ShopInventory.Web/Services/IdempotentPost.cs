using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// Sends a document create with an <c>Idempotency-Key</c> the API can claim.
/// </summary>
/// <remarks>
/// The key is minted once per document entry and kept on the request, which is also what the page's
/// saved draft holds (see <see cref="FormDraft"/>). So every retry of the same entry, including one
/// made after a reload that restored the draft, sends the same key, and the API answers it with the
/// document it already created instead of creating another. A fresh key per call, which some of
/// these routes used to send, deduplicates nothing.
/// </remarks>
internal static class IdempotentPost
{
    /// <summary>The key already on the request, or a new one when it has none.</summary>
    public static string EnsureKey(string? clientRequestId) =>
        string.IsNullOrWhiteSpace(clientRequestId) ? Guid.NewGuid().ToString("N") : clientRequestId.Trim();

    public static Task<HttpResponseMessage> PostAsJsonAsync<TRequest>(
        HttpClient httpClient,
        string requestUri,
        TRequest request,
        string clientRequestId,
        CancellationToken cancellationToken = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", clientRequestId);
        return httpClient.SendAsync(message, cancellationToken);
    }
}
