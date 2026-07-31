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
