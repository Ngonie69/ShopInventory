using ShopInventory.Web.Models;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ShopInventory.Web.Services;

public interface IAuthService
{
    Task<(bool Success, string Message, LoginResponse? Response)> LoginAsync(string username, string password);
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
                _logger.LogDebug("Login response parsed: AccessToken={HasToken}, User={Username}",
                    !string.IsNullOrEmpty(loginResponse?.AccessToken),
                    loginResponse?.User?.Username);

                if (loginResponse != null)
                {
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
