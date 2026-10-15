namespace ShopInventory.Web.Services;

/// <summary>
/// Takes the Web's <c>X-API-Key</c> off a request that carries no bearer token and comes from a
/// circuit, unless that circuit is a proved portal customer's. Every other request passes untouched.
/// </summary>
/// <remarks>
/// See <see cref="WebApiCaller"/>. The key stays on a request with a bearer token, which the API
/// judges by the user (and refuses outright when the token fails), and on one made outside any
/// circuit, which is the Web's own work. Without the key, a tokenless send from a page reaches the API
/// as what it is: anonymous, whether or not somebody is signed in on the circuit. Routes that admit
/// anyone still answer it; the rest answer 401.
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
            WebApiCaller.Current is { MaySendTheKeyAlone: false })
        {
            request.Headers.Remove(HeaderName);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
