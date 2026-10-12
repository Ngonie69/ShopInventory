using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// Sends every API call a Blazor circuit makes on that circuit's own access token, renewed first
/// when it has run out.
/// </summary>
/// <remarks>
/// <para>
/// The circuit's shared <see cref="HttpClient"/> carries whatever bearer was last attached to it,
/// and only navigation attached one. A page that stayed open and kept calling outlived the hour its
/// token was good for. Where the route authenticates the bearer alone the call was refused and the
/// page said "Your session has expired" to somebody still signed in. Where the route also accepts
/// the portal's API key, which is most of them, nothing was refused: the call was answered as the
/// key, with the key's roles and the key's name, for whoever had the page open.
/// </para>
/// <para>
/// This asks <see cref="CustomAuthStateProvider"/> for a valid token as each request leaves, which
/// costs nothing while the cached token is good and renews it from the refresh token when it is
/// not. A token the API rejects anyway is renewed and the request sent once more, as
/// <see cref="ApiTokenAuthentication.SendAsync"/> does. It is built by <see cref="CircuitApiClient"/>
/// around the client factory's pipeline, once per circuit, which is what lets it reach that
/// circuit's provider.
/// </para>
/// <para>
/// What it leaves exactly as the caller built it:
/// </para>
/// <list type="bullet">
/// <item>A request to any other host. The token is the API's and goes nowhere else.</item>
/// <item>A request that already carries credentials of another kind.</item>
/// <item>The provider's own calls, made inside <see cref="SendAsIs"/>: the refresh goes out on this
/// client while the provider holds its refresh lock, and asking it for a token from in here would
/// wait on that lock for ever.</item>
/// <item>A refused request whose body cannot be read twice, a streamed upload. It is handed back
/// unretried, and a caller that needs the retry builds the request per attempt through
/// <see cref="ApiTokenAuthentication.SendAsync"/>.</item>
/// </list>
/// <para>
/// A scope that is no circuit, a background job, has no browser to read a token from: the provider
/// answers none and the request goes out as it was, on the API key.
/// </para>
/// </remarks>
internal sealed class CircuitApiTokenHandler(
    IServiceProvider circuitServices,
    ILogger<CircuitApiTokenHandler> logger) : DelegatingHandler
{
    private static readonly AsyncLocal<bool> AsIs = new();

    private CustomAuthStateProvider? _authStateProvider;

    /// <summary>The API this circuit's client talks to. Requests to anywhere else pass untouched.</summary>
    public Uri? ApiBaseAddress { get; set; }

    /// <summary>
    /// Sends the requests made until the returned scope is disposed without asking for a token.
    /// For <see cref="CustomAuthStateProvider"/>'s own calls.
    /// </summary>
    public static IDisposable SendAsIs()
    {
        var previous = AsIs.Value;
        AsIs.Value = true;
        return new Scope(previous);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (AsIs.Value || !IsForTheApi(request) || CarriesOtherCredentials(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var authStateProvider = AuthStateProvider();
        if (authStateProvider is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var token = await ValidTokenAsync(authStateProvider);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !CanBeSentAgain(request.Content))
        {
            return response;
        }

        var rejectedToken = request.Headers.Authorization?.Parameter;
        var renewedToken = await RenewAsync(authStateProvider, rejectedToken);

        // Whether or not it worked, the renewal for this refusal has been tried: a caller wrapped in
        // ApiTokenAuthentication.SendAsync must not ask the API for a second one.
        request.Options.Set(ApiTokenAuthentication.RenewedAfterUnauthorized, true);
        response.RequestMessage ??= request;

        if (string.IsNullOrWhiteSpace(renewedToken) ||
            string.Equals(renewedToken, rejectedToken, StringComparison.Ordinal))
        {
            // The session could not be renewed, so the 401 is genuine.
            return response;
        }

        logger.LogInformation(
            "Renewed an expired access token after a 401 from {Method} {Path}",
            request.Method,
            request.RequestUri?.AbsolutePath);

        response.Dispose();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", renewedToken);

        var retried = await base.SendAsync(request, cancellationToken);
        retried.RequestMessage ??= request;
        return retried;
    }

    private bool IsForTheApi(HttpRequestMessage request) =>
        ApiBaseAddress is not null &&
        request.RequestUri is { IsAbsoluteUri: true } uri &&
        Uri.Compare(
            uri,
            ApiBaseAddress,
            UriComponents.SchemeAndServer,
            UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;

    private static bool CarriesOtherCredentials(HttpRequestMessage request) =>
        request.Headers.Authorization is { } authorization &&
        !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the body can be written a second time. The buffered kinds can; a stream has been
    /// read by the first attempt, and a multipart body may be holding one.
    /// </summary>
    private static bool CanBeSentAgain(HttpContent? content) =>
        content is null or JsonContent or ByteArrayContent or ReadOnlyMemoryContent;

    private CustomAuthStateProvider? AuthStateProvider()
    {
        try
        {
            return _authStateProvider ??= circuitServices.GetRequiredService<CustomAuthStateProvider>();
        }
        catch (ObjectDisposedException)
        {
            // A timer that fired after its circuit was torn down. The request goes as it is.
            return null;
        }
    }

    private async Task<string?> ValidTokenAsync(CustomAuthStateProvider authStateProvider)
    {
        try
        {
            return await authStateProvider.GetAccessTokenAsync();
        }
        catch (Exception ex)
        {
            // Not being able to work out a token must never be what fails a request.
            logger.LogDebug("Could not resolve the API access token: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<string?> RenewAsync(CustomAuthStateProvider authStateProvider, string? rejectedToken)
    {
        try
        {
            return await authStateProvider.RefreshAccessTokenAsync(rejectedToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not renew the API access token: {Message}", ex.Message);
            return null;
        }
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => AsIs.Value = previous;
    }
}
