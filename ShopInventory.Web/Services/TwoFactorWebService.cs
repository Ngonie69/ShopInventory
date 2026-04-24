using System.Net.Http.Headers;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Interface for Two-Factor Authentication web service
/// </summary>
public interface ITwoFactorWebService
{
    /// <summary>
    /// Get 2FA status for the current user
    /// </summary>
    Task<TwoFactorStatusModel?> GetStatusAsync();

    /// <summary>
    /// Initiate 2FA setup - get QR code and secret
    /// </summary>
    Task<TwoFactorSetupModel?> InitiateSetupAsync();

    /// <summary>
    /// Enable 2FA by verifying setup code
    /// </summary>
    Task<(bool Success, string Message)> EnableTwoFactorAsync(string code);

    /// <summary>
    /// Disable 2FA
    /// </summary>
    Task<(bool Success, string Message)> DisableTwoFactorAsync(string password, string code);

    /// <summary>
    /// Regenerate backup codes
    /// </summary>
    Task<List<string>?> RegenerateBackupCodesAsync(string code);

    /// <summary>
    /// Change password for current user
    /// </summary>
    Task<(bool Success, string Message)> ChangePasswordAsync(string username, string currentPassword, string newPassword, string confirmPassword);

    /// <summary>
    /// Get recent login activity for the current user
    /// </summary>
    Task<List<LoginActivityModel>> GetRecentLoginActivityAsync(int count = 10);

    /// <summary>
    /// Get current credentials (username/email) for the current user
    /// </summary>
    Task<CredentialsModel?> GetCredentialsAsync();

    /// <summary>
    /// Update login credentials (username/email) for the current user
    /// </summary>
    Task<(bool Success, string Message, CredentialsModel? Data)> UpdateCredentialsAsync(string? username, string? email, string currentPassword);

    /// <summary>
    /// Get registered passkeys for the current user.
    /// </summary>
    Task<List<PasskeyCredentialInfo>> GetPasskeysAsync();

    /// <summary>
    /// Start passkey registration for the current user.
    /// </summary>
    Task<(bool Success, string Message, (string SessionToken, string OptionsJson)? Ceremony)> BeginPasskeyRegistrationAsync(string friendlyName, string origin, string rpId);

    /// <summary>
    /// Complete passkey registration for the current user.
    /// </summary>
    Task<(bool Success, string Message, PasskeyCredentialInfo? Credential)> CompletePasskeyRegistrationAsync(string sessionToken, string credentialJson, string origin, string rpId);
}

/// <summary>
/// Implementation of Two-Factor Authentication web service
/// </summary>
public class TwoFactorWebService : ITwoFactorWebService
{
    private readonly HttpClient _httpClient;
    private readonly ILocalStorageService _localStorage;
    private readonly CustomAuthStateProvider _authStateProvider;
    private readonly ILogger<TwoFactorWebService> _logger;

    public TwoFactorWebService(
        HttpClient httpClient,
        ILocalStorageService localStorage,
        CustomAuthStateProvider authStateProvider,
        ILogger<TwoFactorWebService> logger)
    {
        _httpClient = httpClient;
        _localStorage = localStorage;
        _authStateProvider = authStateProvider;
        _logger = logger;
    }

    private async Task EnsureAuthenticationAsync()
    {
        try
        {
            var token = await _localStorage.GetItemAsync<string>("authToken");
            var currentToken = _httpClient.DefaultRequestHeaders.Authorization?.Parameter;

            if (string.IsNullOrWhiteSpace(token))
            {
                _httpClient.DefaultRequestHeaders.Authorization = null;
                return;
            }

            if (!string.Equals(currentToken, token, StringComparison.Ordinal))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not access localStorage for auth token: {Message}", ex.Message);
        }
    }

    public async Task<TwoFactorStatusModel?> GetStatusAsync()
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.GetAsync("api/twofactor/status");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TwoFactorStatusModel>();
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to get 2FA status: {Error}", error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting 2FA status");
            return null;
        }
    }

    public async Task<TwoFactorSetupModel?> InitiateSetupAsync()
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.PostAsync("api/twofactor/setup", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TwoFactorSetupModel>();
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to initiate 2FA setup (HTTP {StatusCode}): {Error}", (int)response.StatusCode, error);
            throw new HttpRequestException($"API returned {(int)response.StatusCode}: {error}");
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating 2FA setup");
            throw;
        }
    }

    public async Task<(bool Success, string Message)> EnableTwoFactorAsync(string code)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var request = new { Code = code };
            var response = await _httpClient.PostAsJsonAsync("api/twofactor/enable", request);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Two-factor authentication enabled successfully");
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to enable 2FA: {Error}", errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Failed to enable 2FA");
            }
            catch
            {
                return (false, "Failed to enable 2FA");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enabling 2FA");
            return (false, $"Error: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> DisableTwoFactorAsync(string password, string code)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var request = new { Password = password, Code = code };
            var response = await _httpClient.PostAsJsonAsync("api/twofactor/disable", request);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Two-factor authentication disabled successfully");
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to disable 2FA: {Error}", errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Failed to disable 2FA");
            }
            catch
            {
                return (false, "Failed to disable 2FA");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disabling 2FA");
            return (false, $"Error: {ex.Message}");
        }
    }

    public async Task<List<string>?> RegenerateBackupCodesAsync(string code)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var request = new { Code = code };
            var response = await _httpClient.PostAsJsonAsync("api/twofactor/backup-codes/regenerate", request);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<string>>();
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to regenerate backup codes: {Error}", error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error regenerating backup codes");
            return null;
        }
    }

    public async Task<(bool Success, string Message)> ChangePasswordAsync(string username, string currentPassword, string newPassword, string confirmPassword)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var request = new { Username = username, CurrentPassword = currentPassword, NewPassword = newPassword, ConfirmPassword = confirmPassword };
            var response = await _httpClient.PostAsJsonAsync("api/password/change", request);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Password changed successfully");
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Failed to change password");
            }
            catch
            {
                return (false, "Failed to change password");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password");
            return (false, $"Error: {ex.Message}");
        }
    }

    public async Task<List<LoginActivityModel>> GetRecentLoginActivityAsync(int count = 10)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.GetAsync($"api/useractivity/me?recentCount={count}");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<UserActivityMeResponse>();
                return result?.RecentActivities ?? new List<LoginActivityModel>();
            }
            return new List<LoginActivityModel>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching login activity");
            return new List<LoginActivityModel>();
        }
    }

    public async Task<CredentialsModel?> GetCredentialsAsync()
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.GetAsync("api/password/credentials");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<CredentialsModel>();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching credentials");
            return null;
        }
    }

    public async Task<(bool Success, string Message, CredentialsModel? Data)> UpdateCredentialsAsync(string? username, string? email, string currentPassword)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var request = new { Username = username, Email = email, CurrentPassword = currentPassword };
            var response = await _httpClient.PutAsJsonAsync("api/password/credentials", request);

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<CredentialsModel>();
                if (data != null)
                {
                    await _authStateProvider.UpdateUserProfileAsync(data.Username, data.Email);
                }
                return (true, "Credentials updated successfully", data);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Failed to update credentials", null);
            }
            catch
            {
                return (false, "Failed to update credentials", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating credentials");
            return (false, $"Error: {ex.Message}", null);
        }
    }

    public async Task<List<PasskeyCredentialInfo>> GetPasskeysAsync()
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.GetAsync("api/auth/passkeys");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<PasskeyCredentialInfo>>() ?? new List<PasskeyCredentialInfo>();
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to fetch passkeys: {Error}", error);
            return new List<PasskeyCredentialInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching passkeys");
            return new List<PasskeyCredentialInfo>();
        }
    }

    public async Task<(bool Success, string Message, (string SessionToken, string OptionsJson)? Ceremony)> BeginPasskeyRegistrationAsync(
        string friendlyName, string origin, string rpId)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.PostAsJsonAsync("api/auth/passkeys/register/options", new
            {
                FriendlyName = friendlyName,
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
            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Failed to start passkey registration", null);
            }
            catch
            {
                return (false, "Failed to start passkey registration", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting passkey registration");
            return (false, $"Error: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message, PasskeyCredentialInfo? Credential)> CompletePasskeyRegistrationAsync(
        string sessionToken, string credentialJson, string origin, string rpId)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.PostAsJsonAsync("api/auth/passkeys/register/complete", new
            {
                SessionToken = sessionToken,
                CredentialJson = credentialJson,
                Origin = origin,
                RpId = rpId
            });

            if (response.IsSuccessStatusCode)
            {
                var credential = await response.Content.ReadFromJsonAsync<PasskeyCredentialInfo>();
                return credential != null
                    ? (true, "Passkey added successfully", credential)
                    : (false, "Failed to read passkey registration response", null);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (false, errorResponse?.Message ?? "Failed to complete passkey registration", null);
            }
            catch
            {
                return (false, "Failed to complete passkey registration", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey registration");
            return (false, $"Error: {ex.Message}", null);
        }
    }
}

// Models for 2FA
public class TwoFactorStatusModel
{
    public bool IsEnabled { get; set; }
    public int BackupCodesRemaining { get; set; }
    public DateTime? EnabledAt { get; set; }
}

public class TwoFactorSetupModel
{
    public string SecretKey { get; set; } = string.Empty;
    public string QrCodeUri { get; set; } = string.Empty;
    public string ManualEntryKey { get; set; } = string.Empty;
    public List<string> BackupCodes { get; set; } = new();
}

public class LoginActivityModel
{
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? PageUrl { get; set; }
    public bool IsSuccess { get; set; }
    public DateTime Timestamp { get; set; }

    public string TimeAgo
    {
        get
        {
            var diff = DateTime.UtcNow - Timestamp;
            if (diff.TotalMinutes < 1) return "Just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return Timestamp.ToString("MMM dd, yyyy");
        }
    }
}

public class UserActivityMeResponse
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public int TotalActions { get; set; }
    public int ActionsToday { get; set; }
    public int ActionsThisWeek { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public string? LastAction { get; set; }
    public List<LoginActivityModel> RecentActivities { get; set; } = new();
}

public class CredentialsModel
{
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Message { get; set; } = string.Empty;
}
