using System.Net.Http.Json;
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
}

/// <summary>
/// Implementation of Two-Factor Authentication web service
/// </summary>
public class TwoFactorWebService : ITwoFactorWebService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TwoFactorWebService> _logger;

    public TwoFactorWebService(HttpClient httpClient, ILogger<TwoFactorWebService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TwoFactorStatusModel?> GetStatusAsync()
    {
        try
        {
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
            var response = await _httpClient.PostAsync("api/twofactor/setup", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TwoFactorSetupModel>();
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to initiate 2FA setup: {Error}", error);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating 2FA setup");
            return null;
        }
    }

    public async Task<(bool Success, string Message)> EnableTwoFactorAsync(string code)
    {
        try
        {
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
