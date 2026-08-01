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
    private readonly AuthenticationState _anonymous;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // How long to wait before retrying a refresh that failed for a transient reason. Callers that
    // poll for a token (PodService) would otherwise hit the API's auth rate limit.
    private static readonly TimeSpan RefreshRetryBackoff = TimeSpan.FromSeconds(5);

    // In-memory cache for auth state during same session
    private string? _cachedToken;
    private UserInfo? _cachedUserInfo;
    private DateTime? _cachedExpiresAt;
    private DateTime? _lastRefreshFailureAt;

    private enum RefreshOutcome
    {
        Succeeded,
        Rejected,
        Failed
    }

    public CustomAuthStateProvider(ILocalStorageService localStorage, HttpClient httpClient, ILogger<CustomAuthStateProvider> logger)
    {
        _localStorage = localStorage;
        _httpClient = httpClient;
        _logger = logger;
        _anonymous = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
    }

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

            var userInfo = _cachedUserInfo ?? await _localStorage.GetItemAsync<UserInfo>("userInfo");
            _cachedUserInfo = userInfo;

            _logger.LogInformation("Authenticated user from localStorage: {Username}", userInfo?.Username);
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
                    _cachedUserInfo ??= await _localStorage.GetItemAsync<UserInfo>("userInfo");
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
            var response = await _httpClient.PostAsJsonAsync("api/auth/refresh", request);

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

    private async Task SignOutAsync()
    {
        await ClearAuthData();
        NotifyUserLogout();
    }

    private async Task<DateTime?> ResolveExpiryAsync(string token)
    {
        DateTime? storedExpiry = null;
        try
        {
            storedExpiry = await _localStorage.GetItemAsync<DateTime?>("tokenExpiresAt");
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not read token expiry from localStorage: {Message}", ex.Message);
        }

        return NormalizeUtc(storedExpiry) ?? ReadExpiryFromToken(token);
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

    private static bool IsUsable(DateTime? expiresAt)
        => !expiresAt.HasValue || expiresAt.Value >= DateTime.UtcNow.AddMinutes(2);

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

        if (userInfo?.AllowedPaymentMethods != null)
        {
            foreach (var pm in userInfo.AllowedPaymentMethods)
            {
                claims.Add(new Claim("paymentmethod", pm));
            }
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
        _logger.LogDebug("In-memory auth cache updated. ExpiresAt: {ExpiresAt}", loginResponse.ExpiresAt);

        try
        {
            _logger.LogDebug("Storing auth data to localStorage");
            await _localStorage.SetItemAsync("authToken", loginResponse.AccessToken);
            await _localStorage.SetItemAsync("refreshToken", loginResponse.RefreshToken);
            await _localStorage.SetItemAsync("tokenExpiresAt", loginResponse.ExpiresAt);
            await _localStorage.SetItemAsync("userInfo", loginResponse.User);
            _logger.LogInformation("Auth data stored to localStorage successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store auth data to localStorage");
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResponse.AccessToken);
        _logger.LogDebug("HttpClient Authorization header set");
    }

    public async Task UpdateUserProfileAsync(string username, string? email)
    {
        _logger.LogInformation("Updating cached auth profile for user: {Username}", username);

        _cachedUserInfo ??= await _localStorage.GetItemAsync<UserInfo>("userInfo") ?? new UserInfo();
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

        try
        {
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
