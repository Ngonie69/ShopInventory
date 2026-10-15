using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public class CustomAuthStateProvider : AuthenticationStateProvider
{
    private readonly ILocalStorageService _localStorage;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CustomAuthStateProvider> _logger;
    private readonly WebClientAuditContext _clientAuditContext;
    private readonly AuthenticationState _anonymous;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // How long to wait before retrying a refresh that failed for a transient reason. Callers that
    // poll for a token (PodService) would otherwise hit the API's auth rate limit.
    private static readonly TimeSpan RefreshRetryBackoff = TimeSpan.FromSeconds(5);

    // In-memory cache for auth state during same session
    private string? _cachedToken;

    // Who is signed in, as the API last described them: from a sign-in or refresh response, or from
    // api/auth/me. Never read from the browser. Anyone at the keyboard can write localStorage, so a
    // "userInfo" taken from it is whatever they typed, Admin included.
    private UserInfo? _cachedUserInfo;

    // The api/auth/me answer for one token, shared by the callers that ask while it is in flight, so a
    // circuit asks the API once rather than once per component.
    private string? _verifyingToken;
    private Task<UserInfo?>? _verifying;
    private PasswordStandingInfo? _cachedPasswordStanding;
    private const string PasswordStandingKey = "passwordStanding";
    private DateTime? _cachedExpiresAt;
    private DateTime? _lastRefreshFailureAt;

    private enum RefreshOutcome
    {
        Succeeded,
        Rejected,
        Failed
    }

    private enum VerifyOutcome
    {
        Verified,
        Rejected,
        Failed
    }

    public CustomAuthStateProvider(
        ILocalStorageService localStorage,
        HttpClient httpClient,
        ILogger<CustomAuthStateProvider> logger,
        WebClientAuditContext clientAuditContext)
    {
        _localStorage = localStorage;
        _httpClient = httpClient;
        _logger = logger;
        _clientAuditContext = clientAuditContext;
        _anonymous = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
    }

    /// <summary>
    /// Whether this circuit holds a user the API described: signed in, refreshed or confirmed by
    /// api/auth/me, and not signed out since. Read by <see cref="WebApiCaller"/> without touching the
    /// browser, so it is safe from an HTTP handler.
    /// </summary>
    public bool HasConfirmedUser => _cachedUserInfo is not null;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        _logger.LogDebug("GetAuthenticationStateAsync called");

        try
        {
            // First check in-memory cache (for same session)
            if (!string.IsNullOrWhiteSpace(_cachedToken) && _cachedUserInfo != null)
            {
                // Check if cached token is expired or about to expire (2 min buffer)
                if (!HasUsableCachedToken())
                {
                    _logger.LogDebug("Cached token expired or expiring soon, attempting refresh");
                    var refreshedToken = await RefreshAccessTokenAsync();
                    if (string.IsNullOrWhiteSpace(refreshedToken))
                    {
                        _logger.LogWarning("Token refresh failed from cache path");
                        return _anonymous;
                    }

                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
                    return CreateAuthState(_cachedUserInfo);
                }

                _logger.LogDebug("Using cached auth state for user: {Username}", _cachedUserInfo.Username);
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken);
                return CreateAuthState(_cachedUserInfo);
            }

            // Try to get from localStorage
            string? token = null;
            try
            {
                _logger.LogDebug("Attempting to read token from localStorage");
                token = await _localStorage.GetItemAsync<string>("authToken");
                _logger.LogDebug("Token from localStorage: {HasToken}", !string.IsNullOrWhiteSpace(token));
            }
            catch (Exception ex)
            {
                // localStorage not available (prerendering)
                _logger.LogDebug("localStorage not available (likely prerendering): {Message}", ex.Message);
                return _anonymous;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogDebug("No token found, returning anonymous state");
                return _anonymous;
            }

            var expiresAt = await ResolveExpiryAsync(token);
            _logger.LogDebug("Token expires at: {ExpiresAt}, Current UTC: {Now}", expiresAt, DateTime.UtcNow);

            if (!IsUsable(expiresAt))
            {
                _logger.LogDebug("Token expired or expiring soon, attempting refresh");
                var refreshedToken = await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(refreshedToken))
                {
                    return _anonymous;
                }

                token = refreshedToken;
                expiresAt = _cachedExpiresAt;
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _cachedToken = token;
            // Without this the cached branch above treats the token as valid for the life of the
            // circuit, so an expired token is sent to the API while the UI still looks signed in.
            _cachedExpiresAt = expiresAt;

            // A refresh above has already brought the user back from the API. Otherwise the token is
            // only something the browser holds, and the API is asked who it belongs to.
            var userInfo = _cachedUserInfo ?? await VerifyUserOnceAsync(token);
            if (userInfo is null)
            {
                return _anonymous;
            }

            _logger.LogInformation("Authenticated user from the API: {Username}", userInfo.Username);
            return CreateAuthState(userInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetAuthenticationStateAsync");
            return _anonymous;
        }
    }

    /// <summary>
    /// Returns an access token that is still valid, refreshing it first when it has expired or is
    /// about to. Returns null when the session can no longer be renewed.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        if (HasUsableCachedToken())
        {
            return _cachedToken;
        }

        try
        {
            var token = await _localStorage.GetItemAsync<string>("authToken");
            if (!string.IsNullOrWhiteSpace(token))
            {
                var expiresAt = await ResolveExpiryAsync(token);
                if (IsUsable(expiresAt))
                {
                    _cachedToken = token;
                    _cachedExpiresAt = expiresAt;
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return token;
                }
            }
            else if (string.IsNullOrWhiteSpace(_cachedToken))
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            // localStorage is unavailable during prerendering - fall back to whatever we hold.
            _logger.LogDebug("Could not read auth token from localStorage: {Message}", ex.Message);
            return _cachedToken;
        }

        return await RefreshAccessTokenAsync();
    }

    /// <summary>
    /// Renews the access token from the stored refresh token. Pass the token the API just rejected
    /// so a cached copy of it is not handed straight back to the caller.
    /// </summary>
    public async Task<string?> RefreshAccessTokenAsync(string? rejectedToken = null)
    {
        string? token = null;
        var signOut = false;

        await _refreshLock.WaitAsync();
        try
        {
            // Another caller may have refreshed while we waited on the lock.
            if (HasUsableCachedToken() && !string.Equals(_cachedToken, rejectedToken, StringComparison.Ordinal))
            {
                return _cachedToken;
            }

            if (_lastRefreshFailureAt.HasValue &&
                DateTime.UtcNow - _lastRefreshFailureAt.Value < RefreshRetryBackoff)
            {
                _logger.LogDebug("Skipping refresh, the previous attempt failed moments ago");
                return null;
            }

            string? refreshToken = null;
            try
            {
                refreshToken = await _localStorage.GetItemAsync<string>("refreshToken");
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not read refresh token from localStorage: {Message}", ex.Message);
                return null;
            }

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogWarning("No refresh token available, signing the user out");
                signOut = true;
            }
            else
            {
                switch (await RequestRefreshAsync(refreshToken))
                {
                    case RefreshOutcome.Succeeded:
                        _logger.LogInformation("Access token refreshed");
                        _lastRefreshFailureAt = null;
                        token = _cachedToken;
                        break;

                    case RefreshOutcome.Rejected:
                        _logger.LogWarning("Refresh token rejected, signing the user out");
                        signOut = true;
                        break;

                    default:
                        // Transient failure (network/server) - keep the session so the user can retry.
                        _lastRefreshFailureAt = DateTime.UtcNow;
                        break;
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        // Done outside the lock: notifying the auth state change re-enters GetAuthenticationStateAsync,
        // which can ask for a refresh again, and the semaphore is not reentrant.
        if (signOut)
        {
            await SignOutAsync();
        }

        return token;
    }

    private async Task<RefreshOutcome> RequestRefreshAsync(string refreshToken)
    {
        try
        {
            var request = new RefreshTokenRequest { RefreshToken = refreshToken };
            var response = await _httpClient.PostAsJsonWithClientAuditAsync(
                "api/auth/refresh",
                request,
                _clientAuditContext);

            if (response.IsSuccessStatusCode)
            {
                var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (loginResponse != null && !string.IsNullOrWhiteSpace(loginResponse.AccessToken))
                {
                    await StoreAuthData(loginResponse);
                    return RefreshOutcome.Succeeded;
                }

                _logger.LogWarning("Token refresh returned no access token");
                return RefreshOutcome.Failed;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.BadRequest)
            {
                _logger.LogWarning("Token refresh rejected with status {StatusCode}", (int)response.StatusCode);
                return RefreshOutcome.Rejected;
            }

            _logger.LogWarning("Token refresh failed with status {StatusCode}", (int)response.StatusCode);
            return RefreshOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token refresh request failed");
            return RefreshOutcome.Failed;
        }
    }

    private Task<UserInfo?> VerifyUserOnceAsync(string token)
    {
        if (_verifying is null || !string.Equals(_verifyingToken, token, StringComparison.Ordinal))
        {
            _verifyingToken = token;
            _verifying = VerifyUserAsync(token);
        }

        return _verifying;
    }

    /// <summary>
    /// Asks the API who the token belongs to, and takes the user, role included, from its answer.
    /// A token it refuses is renewed once from the refresh token; one that cannot be renewed signs
    /// the browser out. Returns null when there is no user to show.
    /// </summary>
    private async Task<UserInfo?> VerifyUserAsync(string token)
    {
        var (outcome, user) = await RequestCurrentUserAsync(token);

        if (outcome == VerifyOutcome.Rejected)
        {
            // Expired early, revoked, or never issued by the API at all. A refresh brings the user
            // back with the new token; failing that, RefreshAccessTokenAsync has signed the browser out.
            _logger.LogWarning("The API did not accept the stored access token, renewing it");
            var renewed = await RefreshAccessTokenAsync(token);
            if (string.IsNullOrWhiteSpace(renewed))
            {
                ForgetVerification();
                return null;
            }

            if (_cachedUserInfo is not null)
            {
                return _cachedUserInfo;
            }

            (outcome, user) = await RequestCurrentUserAsync(renewed);
        }

        if (outcome != VerifyOutcome.Verified || user is null)
        {
            // A refusal of a freshly renewed token, or the API out of reach. Show no one, but keep the
            // browser's session so the next page load can ask again.
            _logger.LogWarning("Could not confirm the signed-in user with the API ({Outcome})", outcome);
            ForgetVerification();
            return null;
        }

        _cachedUserInfo = user;
        return user;
    }

    private async Task<(VerifyOutcome Outcome, UserInfo? User)> RequestCurrentUserAsync(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/auth/me")
                .WithClientAudit(_clientAuditContext);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (VerifyOutcome.Rejected, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("api/auth/me answered {StatusCode}", (int)response.StatusCode);
                return (VerifyOutcome.Failed, null);
            }

            var user = await response.Content.ReadFromJsonAsync<UserInfo>();
            if (user is null || string.IsNullOrWhiteSpace(user.Username) || string.IsNullOrWhiteSpace(user.Role))
            {
                _logger.LogWarning("api/auth/me did not name a user and a role");
                return (VerifyOutcome.Failed, null);
            }

            return (VerifyOutcome.Verified, user);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "api/auth/me request failed");
            return (VerifyOutcome.Failed, null);
        }
    }

    private void ForgetVerification()
    {
        _verifyingToken = null;
        _verifying = null;
    }

    /// <summary>
    /// Asks the API to revoke this browser's refresh token, so signing out ends the session on the
    /// server too and not only in this browser. Best effort: a failure is logged and the caller
    /// clears the browser either way.
    /// </summary>
    public async Task RevokeRefreshTokenAsync()
    {
        try
        {
            // The logout route needs a valid access token, and getting one may rotate the refresh
            // token, so the refresh token is read after it.
            var accessToken = await GetAccessTokenAsync();
            var refreshToken = await _localStorage.GetItemAsync<string>("refreshToken");
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogDebug("No session to revoke on the API");
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout")
            {
                Content = JsonContent.Create(new RefreshTokenRequest { RefreshToken = refreshToken })
            }.WithClientAudit(_clientAuditContext);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("The API refused to revoke the refresh token at sign-out: {StatusCode}", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not revoke the refresh token at sign-out");
        }
    }

    private async Task SignOutAsync()
    {
        await ClearAuthData();
        NotifyUserLogout();
    }

    /// <summary>
    /// When the token stops being usable: its own "exp" claim, which outranks anything the browser
    /// stored beside it. The stored tokenExpiresAt is read only for a token that carries no readable
    /// expiry, and with neither the expiry is unknown, which <see cref="IsUsable"/> treats as unusable.
    /// </summary>
    private async Task<DateTime?> ResolveExpiryAsync(string token)
    {
        if (ReadExpiryFromToken(token) is { } tokenExpiry)
        {
            return tokenExpiry;
        }

        try
        {
            return NormalizeUtc(await _localStorage.GetItemAsync<DateTime?>("tokenExpiresAt"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not read token expiry from localStorage: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads the "exp" claim straight off the JWT so a missing or cleared tokenExpiresAt entry
    /// cannot make an expired token look valid.
    /// </summary>
    private static DateTime? ReadExpiryFromToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
        }
        catch (Exception)
        {
            // Not a readable JWT - treat the expiry as unknown.
        }

        return null;
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        // default(DateTime) means the value was never populated - treat it as unknown rather than
        // as an expiry in the distant past.
        if (!value.HasValue || value.Value == default)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    // An unknown expiry is not a token that never expires. It is renewed (or the browser signed out)
    // rather than sent as if it were good.
    private static bool IsUsable(DateTime? expiresAt)
        => expiresAt.HasValue && expiresAt.Value >= DateTime.UtcNow.AddMinutes(2);

    private bool HasUsableCachedToken()
    {
        return !string.IsNullOrWhiteSpace(_cachedToken) && IsUsable(_cachedExpiresAt);
    }

    private AuthenticationState CreateAuthState(UserInfo? userInfo)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, userInfo?.Username ?? "User"),
            new Claim(ClaimTypes.Role, userInfo?.Role ?? "User")
        };

        if (!string.IsNullOrEmpty(userInfo?.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, userInfo.Email));
        }

        if (userInfo?.AssignedWarehouseCodes != null)
        {
            foreach (var wh in userInfo.AssignedWarehouseCodes)
            {
                claims.Add(new Claim("warehouse", wh));
            }
        }
        else if (!string.IsNullOrEmpty(userInfo?.AssignedWarehouseCode))
        {
            claims.Add(new Claim("warehouse", userInfo.AssignedWarehouseCode));
        }

        var identity = new ClaimsIdentity(claims, "jwt");
        var user = new ClaimsPrincipal(identity);

        return new AuthenticationState(user);
    }

    public async Task<bool> TryRefreshToken(string refreshToken)
        => await RequestRefreshAsync(refreshToken) == RefreshOutcome.Succeeded;

    public async Task StoreAuthData(LoginResponse loginResponse)
    {
        _logger.LogInformation("StoreAuthData called for user: {Username}", loginResponse.User?.Username);

        // Update in-memory cache first
        _cachedToken = loginResponse.AccessToken;
        _cachedUserInfo = loginResponse.User ?? _cachedUserInfo;
        _cachedExpiresAt = NormalizeUtc(loginResponse.ExpiresAt) ?? ReadExpiryFromToken(loginResponse.AccessToken);
        _cachedPasswordStanding = PasswordStandingInfo.From(loginResponse);
        _logger.LogDebug("In-memory auth cache updated. ExpiresAt: {ExpiresAt}", loginResponse.ExpiresAt);

        try
        {
            _logger.LogDebug("Storing auth data to localStorage");
            await _localStorage.SetItemAsync("authToken", loginResponse.AccessToken);
            await _localStorage.SetItemAsync("refreshToken", loginResponse.RefreshToken);
            await _localStorage.SetItemAsync("tokenExpiresAt", loginResponse.ExpiresAt);
            await _localStorage.SetItemAsync("userInfo", loginResponse.User);
            await _localStorage.SetItemAsync(PasswordStandingKey, _cachedPasswordStanding);
            _logger.LogInformation("Auth data stored to localStorage successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store auth data to localStorage");
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResponse.AccessToken);
        _logger.LogDebug("HttpClient Authorization header set");
    }

    /// <summary>
    /// Whether this account owes a password change, as the API last said when it issued a token.
    /// </summary>
    /// <remarks>
    /// Null when nothing is known: signed out, or a session that began before the API reported it.
    /// Read from the browser when this circuit has not seen a sign-in of its own — a second tab, or
    /// a reconnect — so it can only be called once the page is interactive.
    /// </remarks>
    public async Task<PasswordStandingInfo?> GetPasswordStandingAsync()
    {
        if (_cachedPasswordStanding is not null)
        {
            return _cachedPasswordStanding;
        }

        try
        {
            _cachedPasswordStanding = await _localStorage.GetItemAsync<PasswordStandingInfo>(PasswordStandingKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not read password standing from localStorage: {Message}", ex.Message);
        }

        return _cachedPasswordStanding;
    }

    public async Task UpdateUserProfileAsync(string username, string? email)
    {
        _logger.LogInformation("Updating cached auth profile for user: {Username}", username);

        // Only a user the API has already confirmed is edited. Reading one back from the browser here
        // would bring its role along with it.
        if (_cachedUserInfo is null)
        {
            _logger.LogWarning("No confirmed user to update the profile of");
            return;
        }

        _cachedUserInfo.Username = username;
        _cachedUserInfo.Email = email;

        try
        {
            await _localStorage.SetItemAsync("userInfo", _cachedUserInfo);
            _logger.LogDebug("Updated cached userInfo in localStorage");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update cached userInfo in localStorage");
        }

        NotifyAuthenticationStateChanged(Task.FromResult(CreateAuthState(_cachedUserInfo)));
    }

    public async Task ClearAuthData()
    {
        _logger.LogInformation("ClearAuthData called");

        // Clear in-memory cache
        _cachedToken = null;
        _cachedUserInfo = null;
        _cachedExpiresAt = null;
        _cachedPasswordStanding = null;
        ForgetVerification();

        try
        {
            await _localStorage.RemoveItemAsync(PasswordStandingKey);
            await _localStorage.RemoveItemAsync("authToken");
            await _localStorage.RemoveItemAsync("refreshToken");
            await _localStorage.RemoveItemAsync("tokenExpiresAt");
            await _localStorage.RemoveItemAsync("userInfo");
            _logger.LogDebug("localStorage cleared");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear localStorage");
        }

        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    public void NotifyUserAuthentication(string username, string role)
    {
        _logger.LogInformation("NotifyUserAuthentication called for user: {Username}, role: {Role}", username, role);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role)
        };

        var identity = new ClaimsIdentity(claims, "jwt");
        var user = new ClaimsPrincipal(identity);

        _logger.LogDebug("Notifying authentication state changed");
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
        _logger.LogInformation("Authentication state change notified");
    }

    public void NotifyUserLogout()
    {
        _logger.LogInformation("NotifyUserLogout called");
        NotifyAuthenticationStateChanged(Task.FromResult(_anonymous));
    }
}
