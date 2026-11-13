namespace ShopInventory.Web.Services;

/// <summary>
/// Puts <see cref="WebApiClientIdentity.SurfaceHeaderName"/> on a request made for a circuit: the
/// customer portal or a staff page, by <see cref="WebApiCaller.Surface"/>. A request made outside any
/// circuit — the Web's background work — goes without it.
/// </summary>
/// <remarks>
/// The customer portal and the Web's cache sweeps both reach the API with the key and no user token, so
/// the API's call timings could not tell them apart. This header is what separates them there. It is a
/// label for counting, never a credential: the API grants nothing on it.
/// </remarks>
public sealed class WebSurfaceHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Remove(WebApiClientIdentity.SurfaceHeaderName);
        if (WebApiCaller.Current is { } caller)
        {
            request.Headers.TryAddWithoutValidation(WebApiClientIdentity.SurfaceHeaderName, caller.Surface);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
