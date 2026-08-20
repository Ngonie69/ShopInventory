using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// Stamps the browser's address and user agent onto a request the Web app makes to the API.
/// </summary>
/// <remarks>
/// The Web app calls the API server-to-server, so without these headers every browser user reaches
/// it as the loopback address. Production showed the cost plainly: nine of thirty-two auth events in
/// a nine-hour log recorded <c>from IP: ::1</c>, and both of the day's failed logins were among them
/// — unattributable. Twenty-four places in the API key rate limiting, lockout and audit on the
/// connection address, so a brute-force attempt through the web UI is invisible and an IP lockout
/// either catches every web user at once or nothing at all.
/// <para>
/// The API trusts these only from a loopback peer (<c>ForwardedHeadersOptions.KnownProxies</c>
/// defaults to loopback), which is exactly this topology and no wider.
/// </para>
/// <para>
/// This is applied per call site rather than by a <see cref="DelegatingHandler"/> on the API client.
/// <see cref="WebClientAuditContext"/> is scoped to the Blazor circuit, and a handler built by
/// <see cref="IHttpClientFactory"/> resolves from its own handler scope — it would get an empty
/// context, not the circuit's. Keeping one implementation here is what stops the call sites drifting
/// apart, which is how the auth path came to be missed in the first place.
/// </para>
/// </remarks>
public static class ClientAuditHeaders
{
    public const string ForwardedFor = "X-Forwarded-For";

    /// <summary>Adds the client audit headers this circuit knows about, if any.</summary>
    public static HttpRequestMessage WithClientAudit(
        this HttpRequestMessage request,
        WebClientAuditContext? clientAuditContext)
    {
        if (clientAuditContext?.ForwardableIpAddress is { } clientIpAddress)
        {
            request.Headers.TryAddWithoutValidation(ForwardedFor, clientIpAddress);
        }

        if (!string.IsNullOrWhiteSpace(clientAuditContext?.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", clientAuditContext.UserAgent);
        }

        return request;
    }

    /// <summary>
    /// The <c>PostAsJsonAsync</c> equivalent that carries the client audit headers.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClientJsonExtensions.PostAsJsonAsync{TValue}(HttpClient, string?, TValue, CancellationToken)"/>
    /// builds the request internally and never exposes it, so a call site that wants headers has to
    /// build its own. This keeps that from turning into six copies of the same eight lines.
    /// </remarks>
    public static Task<HttpResponseMessage> PostAsJsonWithClientAuditAsync<TValue>(
        this HttpClient httpClient,
        string requestUri,
        TValue value,
        WebClientAuditContext? clientAuditContext,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(value)
        }.WithClientAudit(clientAuditContext);

        return httpClient.SendAsync(request, cancellationToken);
    }
}
