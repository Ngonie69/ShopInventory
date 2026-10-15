namespace ShopInventory.Web.Services;

/// <summary>
/// Takes the Web's <c>X-API-Key</c> off a request that carries no bearer token and comes from a
/// circuit with nobody signed in. Every other request passes untouched.
/// </summary>
/// <remarks>
/// See <see cref="WebApiCaller"/>. The key stays on a request with a bearer token, which the API
/// judges by the user (and refuses outright when the token fails), and on one made outside any
/// circuit, which is the Web's own work. Without the key, a signed-out circuit reaches the API as
/// what it is: anonymous. Routes that admit anyone still answer it; the rest answer 401.
/// </remarks>
public sealed class CircuitApiKeyHandler : DelegatingHandler
{
    public const string HeaderName = "X-API-Key";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // HttpClient has already copied its default headers onto the request, so this sees the bearer
        // a service put on the shared circuit client as well as one set on this request alone.
        if (request.Headers.Authorization is null &&
            WebApiCaller.Current is { HasSignedInCaller: false })
        {
            request.Headers.Remove(HeaderName);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
