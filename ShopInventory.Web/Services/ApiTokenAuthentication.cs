using System.Net;
using System.Net.Http.Headers;
using Blazored.LocalStorage;

namespace ShopInventory.Web.Services;

/// <summary>
/// Applies the current API access token to an <see cref="HttpClient"/>.
///
/// Services must not read "authToken" out of localStorage themselves: the stored token is only as
/// fresh as the last sign-in, so once it expires those calls are rejected with a 401 while the UI
/// still shows the user signed in. Going through <see cref="CustomAuthStateProvider"/> renews an
/// expired token from the refresh token first.
/// </summary>
internal static class ApiTokenAuthentication
{
    /// <summary>
    /// Attaches a valid access token to the client. Returns the token, or null when there is none
    /// to attach.
    /// </summary>
    public static async Task<string?> ApplyAsync(
        HttpClient httpClient,
        CustomAuthStateProvider authStateProvider,
        ILocalStorageService localStorage,
        ILogger? logger = null)
    {
        try
        {
            var token = await authStateProvider.GetAccessTokenAsync()
                        ?? await localStorage.GetItemAsync<string>("authToken");

            return SetBearerToken(httpClient, token);
        }
        catch (Exception ex)
        {
            // localStorage is unavailable during prerendering - keep whatever the client already has.
            logger?.LogDebug("Could not resolve the API access token: {Message}", ex.Message);
            return httpClient.DefaultRequestHeaders.Authorization?.Parameter;
        }
    }

    /// <summary>
    /// Sends an authenticated request, renewing the access token and retrying once when the API
    /// rejects it. The request factory is invoked per attempt because a request message - and any
    /// content attached to it - cannot be resent.
    ///
    /// A retried write may reuse the caller's Idempotency-Key: the API raises the 401 in its
    /// authorization middleware, which runs before <c>UseIdempotency()</c> records the key, so the
    /// retry is processed as new work. Generating a fresh key would instead defeat dedup for a
    /// request that did reach the handler.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        CustomAuthStateProvider authStateProvider,
        ILocalStorageService localStorage,
        Func<Task<HttpResponseMessage>> sendAsync,
        ILogger? logger = null)
    {
        await ApplyAsync(httpClient, authStateProvider, localStorage, logger);
        var response = await sendAsync();

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var renewedToken = await RenewAfterUnauthorizedAsync(httpClient, authStateProvider, logger);
        if (string.IsNullOrWhiteSpace(renewedToken))
        {
            // The session could not be renewed, so the 401 is genuine - hand it back unretried.
            return response;
        }

        response.Dispose();
        return await sendAsync();
    }

    /// <summary>
    /// Renews the access token after the API rejected the current one, and attaches the new token.
    /// Returns null when the session could not be renewed, in which case the 401 is genuine.
    /// </summary>
    public static async Task<string?> RenewAfterUnauthorizedAsync(
        HttpClient httpClient,
        CustomAuthStateProvider authStateProvider,
        ILogger? logger = null)
    {
        var rejectedToken = httpClient.DefaultRequestHeaders.Authorization?.Parameter;
        var renewedToken = await authStateProvider.RefreshAccessTokenAsync(rejectedToken);

        if (string.IsNullOrWhiteSpace(renewedToken) ||
            string.Equals(renewedToken, rejectedToken, StringComparison.Ordinal))
        {
            return null;
        }

        logger?.LogInformation("Renewed an expired access token after a 401 response");
        return SetBearerToken(httpClient, renewedToken);
    }

    private static string? SetBearerToken(HttpClient httpClient, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            httpClient.DefaultRequestHeaders.Authorization = null;
            return null;
        }

        if (!string.Equals(httpClient.DefaultRequestHeaders.Authorization?.Parameter, token, StringComparison.Ordinal))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return token;
    }
}
