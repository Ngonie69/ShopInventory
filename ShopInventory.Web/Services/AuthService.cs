using ShopInventory.Web.Models;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ShopInventory.Web.Services;

public interface IAuthService
{
    Task<(bool Success, string Message, LoginResponse? Response)> LoginAsync(string username, string password);
    Task<(bool Success, string Message, LoginResponse? Response)> CompleteTwoFactorAsync(string twoFactorToken, string code, bool isBackupCode);
    Task<(bool Success, string Message, (string SessionToken, string OptionsJson)? Ceremony)> BeginPasskeyLoginAsync(string origin, string rpId);
    Task<(bool Success, string Message, LoginResponse? Response)> CompletePasskeyLoginAsync(string sessionToken, string credentialJson, string origin, string rpId);
    Task LogoutAsync();
    Task<bool> RefreshTokenAsync();
    Task<(bool Success, string Message, UserInfo? User)> RegisterUserAsync(RegisterUserRequest request);
}

public class AuthService : IAuthService
{
    private readonly HttpClient _httpClient;
    private readonly CustomAuthStateProvider _authStateProvider;
    private readonly ILogger<AuthService> _logger;

    public AuthService(HttpClient httpClient, CustomAuthStateProvider authStateProvider, ILogger<AuthService> logger)
    {
        _httpClient = httpClient;
        _authStateProvider = authStateProvider;
        _logger = logger;
    }

    public async Task<(bool Success, string Message, LoginResponse? Response)> LoginAsync(string username, string password)
    {
        _logger.LogInformation("Login attempt for user: {Username}", username);

        try
        {
            var loginRequest = new LoginRequest
            {
                Username = username,
                Password = password
            };

            _logger.LogDebug("Sending login request to API at {BaseAddress}api/auth/login", _httpClient.BaseAddress);
            var response = await _httpClient.PostAsJsonAsync("api/auth/login", loginRequest);
            _logger.LogDebug("Received response with status code: {StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
                _logger.LogDebug("Login response parsed: RequiresTwoFactor={Requires2FA}, AccessToken={HasToken}, User={Username}",
                    loginResponse?.RequiresTwoFactor,
                    !string.IsNullOrEmpty(loginResponse?.AccessToken),
                    loginResponse?.User?.Username);

                if (loginResponse != null)
                {
                    // When 2FA is required, don't store auth data yet — return the challenge token to the caller
                    if (loginResponse.RequiresTwoFactor)
                    {
                        _logger.LogInformation("2FA required for user login; challenge token received");
                        return (true, "2FA required", loginResponse);
                    }

                    _logger.LogInformation("Storing auth data for user: {Username}", loginResponse.User?.Username);
                    await _authStateProvider.StoreAuthData(loginResponse);

                    _logger.LogInformation("Notifying authentication state change for user: {Username}, Role: {Role}",
                        loginResponse.User?.Username ?? username,
                        loginResponse.User?.Role ?? "User");
                    _authStateProvider.NotifyUserAuthentication(
                        loginResponse.User?.Username ?? username,
                        loginResponse.User?.Role ?? "User");

                    _logger.LogInformation("Login successful for user: {Username}", username);
                    return (true, "Login successful", loginResponse);
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Login failed. Status: {StatusCode}, Error: {Error}", response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Login failed", null);
            }
            catch
            {
                return (false, "Login failed", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during login for user: {Username}", username);
            return (false, $"Error: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message, LoginResponse? Response)> CompleteTwoFactorAsync(
        string twoFactorToken, string code, bool isBackupCode)
    {
        _logger.LogInformation("Completing 2FA login challenge");
        try
        {
            var challengeRequest = new
            {
                TwoFactorToken = twoFactorToken,
                Code = code,
                IsBackupCode = isBackupCode
            };

            var response = await _httpClient.PostAsJsonAsync("api/auth/login/two-factor", challengeRequest);

            if (response.IsSuccessStatusCode)
            {
                var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (loginResponse != null && !string.IsNullOrEmpty(loginResponse.AccessToken))
                {
                    await _authStateProvider.StoreAuthData(loginResponse);
                    _authStateProvider.NotifyUserAuthentication(
                        loginResponse.User?.Username ?? "User",
                        loginResponse.User?.Role ?? "User");
                    _logger.LogInformation("2FA login completed for user: {Username}", loginResponse.User?.Username);
                    return (true, "Login successful", loginResponse);
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("2FA challenge failed. Status: {StatusCode}, Error: {Error}", response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Invalid code", null);
            }
            catch
            {
                return (false, "Invalid code", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during 2FA challenge completion");
            return (false, $"Error: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message, (string SessionToken, string OptionsJson)? Ceremony)> BeginPasskeyLoginAsync(
        string origin, string rpId)
    {
        _logger.LogInformation("Starting passkey login ceremony for RP ID: {RpId}", rpId);

        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/auth/passkeys/login/options", new
            {
                Origin = origin,
                RpId = rpId
            });

            if (response.IsSuccessStatusCode)
            {
                var options = await response.Content.ReadFromJsonAsync<PasskeyOptionsResponse>();
                if (options != null)
                {
                    return (true, "Passkey challenge ready", (options.SessionToken, options.OptionsJson));
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to start passkey login. Status: {StatusCode}, Error: {Error}", response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Failed to start passkey login", null);
            }
            catch
            {
                return (false, "Failed to start passkey login", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during passkey login ceremony start");
            return (false, $"Error: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message, LoginResponse? Response)> CompletePasskeyLoginAsync(
        string sessionToken, string credentialJson, string origin, string rpId)
    {
        _logger.LogInformation("Completing passkey login ceremony");

        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/auth/passkeys/login/complete", new
            {
                SessionToken = sessionToken,
                CredentialJson = credentialJson,
                Origin = origin,
                RpId = rpId
            });

            if (response.IsSuccessStatusCode)
            {
                var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (loginResponse != null && !string.IsNullOrEmpty(loginResponse.AccessToken))
                {
                    await _authStateProvider.StoreAuthData(loginResponse);
                    _authStateProvider.NotifyUserAuthentication(
                        loginResponse.User?.Username ?? "User",
                        loginResponse.User?.Role ?? "User");
                    return (true, "Login successful", loginResponse);
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Passkey login failed. Status: {StatusCode}, Error: {Error}", response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Passkey login failed", null);
            }
            catch
            {
                return (false, "Passkey login failed", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during passkey login completion");
            return (false, $"Error: {ex.Message}", null);
        }
    }

    public async Task LogoutAsync()
    {
        _logger.LogInformation("User logging out");
        await _authStateProvider.ClearAuthData();
        _authStateProvider.NotifyUserLogout();
        _logger.LogInformation("Logout complete");
    }

    public async Task<bool> RefreshTokenAsync()
    {
        // RefreshToken is handled by the auth state provider
        return false;
    }

    public async Task<(bool Success, string Message, UserInfo? User)> RegisterUserAsync(RegisterUserRequest request)
    {
        _logger.LogInformation("Attempting to register new user: {Username}", request.Username);

        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/auth/register", request);

            if (response.IsSuccessStatusCode)
            {
                var userInfo = await response.Content.ReadFromJsonAsync<UserInfo>();
                _logger.LogInformation("Successfully registered user: {Username}", request.Username);
                return (true, "User registered successfully", userInfo);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to register user. Status: {StatusCode}, Error: {Error}",
                response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Registration failed", null);
            }
            catch
            {
                return (false, "Registration failed", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during user registration for: {Username}", request.Username);
            return (false, $"Error: {ex.Message}", null);
        }
    }
}
