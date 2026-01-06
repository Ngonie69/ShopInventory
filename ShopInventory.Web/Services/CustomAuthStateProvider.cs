using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
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

    // In-memory cache for auth state during same session
    private string? _cachedToken;
    private UserInfo? _cachedUserInfo;

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

            var expiresAt = await _localStorage.GetItemAsync<DateTime?>("tokenExpiresAt");
            _logger.LogDebug("Token expires at: {ExpiresAt}, Current UTC: {Now}", expiresAt, DateTime.UtcNow);

            if (expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow)
            {
                _logger.LogDebug("Token expired, attempting refresh");
                // Token expired, try to refresh
                var refreshToken = await _localStorage.GetItemAsync<string>("refreshToken");
                if (!string.IsNullOrWhiteSpace(refreshToken))
                {
                    var refreshed = await TryRefreshToken(refreshToken);
                    if (!refreshed)
                    {
                        _logger.LogWarning("Token refresh failed, clearing auth data");
                        await ClearAuthData();
                        return _anonymous;
                    }
                    token = await _localStorage.GetItemAsync<string>("authToken");
                }
                else
                {
                    _logger.LogWarning("No refresh token available, clearing auth data");
                    await ClearAuthData();
                    return _anonymous;
                }
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _cachedToken = token;

            var userInfo = await _localStorage.GetItemAsync<UserInfo>("userInfo");
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

        var identity = new ClaimsIdentity(claims, "jwt");
        var user = new ClaimsPrincipal(identity);

        return new AuthenticationState(user);
    }

    public async Task<bool> TryRefreshToken(string refreshToken)
    {
        try
        {
            var request = new RefreshTokenRequest { RefreshToken = refreshToken };
            var response = await _httpClient.PostAsJsonAsync("api/auth/refresh", request);

            if (response.IsSuccessStatusCode)
            {
                var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (loginResponse != null)
                {
                    await StoreAuthData(loginResponse);
                    return true;
                }
            }
        }
        catch
        {
            // Refresh failed
        }
        return false;
    }

    public async Task StoreAuthData(LoginResponse loginResponse)
    {
        _logger.LogInformation("StoreAuthData called for user: {Username}", loginResponse.User?.Username);

        // Update in-memory cache first
        _cachedToken = loginResponse.AccessToken;
        _cachedUserInfo = loginResponse.User;
        _logger.LogDebug("In-memory cache updated. Token length: {TokenLength}", loginResponse.AccessToken?.Length ?? 0);

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

    public async Task ClearAuthData()
    {
        _logger.LogInformation("ClearAuthData called");

        // Clear in-memory cache
        _cachedToken = null;
        _cachedUserInfo = null;

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
