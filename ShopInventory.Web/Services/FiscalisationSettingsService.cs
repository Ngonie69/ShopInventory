using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IFiscalisationSettingsService
{
    Task<FiscalisationSettingsResponse?> GetSettingsAsync();
    Task<FiscalisationUpdateResult> UpdateApiKeyAsync(FiscalisationSettingsUpdateRequest request);
    Task<FiscalisationTestConnectionResult> TestConnectionAsync(FiscalisationTestConnectionRequest? request = null);
}

public class FiscalisationSettingsService : IFiscalisationSettingsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FiscalisationSettingsService> _logger;

    public FiscalisationSettingsService(HttpClient httpClient, ILogger<FiscalisationSettingsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<FiscalisationSettingsResponse?> GetSettingsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<FiscalisationSettingsResponse>("api/fiscalisation-settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Fiscalisation settings");
            return null;
        }
    }

    public async Task<FiscalisationUpdateResult> UpdateApiKeyAsync(FiscalisationSettingsUpdateRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync("api/fiscalisation-settings", request);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<FiscalisationUpdateResultResponse>();
                return new FiscalisationUpdateResult
                {
                    Success = true,
                    Message = result?.Message ?? "Fiscalisation API key saved.",
                    ConnectionTestPassed = result?.ConnectionTestPassed,
                    ApiKeyMasked = result?.ApiKeyMasked
                };
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning(
                "Failed to update the Fiscalisation API key: {StatusCode} - {Error}", response.StatusCode, error);

            return new FiscalisationUpdateResult
            {
                Success = false,
                Message = ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    error,
                    "We couldn't save the Fiscalisation API key right now. Please try again.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating the Fiscalisation API key");
            return new FiscalisationUpdateResult
            {
                Success = false,
                Message = ApiErrorResponse.GetFriendlyMessage(
                    ex,
                    "We couldn't save the Fiscalisation API key right now. Please try again.")
            };
        }
    }

    public async Task<FiscalisationTestConnectionResult> TestConnectionAsync(
        FiscalisationTestConnectionRequest? request = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/fiscalisation-settings/test-connection", request);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<FiscalisationTestConnectionResult>();
                return result ?? new FiscalisationTestConnectionResult { Connected = false, Message = "No response" };
            }

            var error = await response.Content.ReadAsStringAsync();
            return new FiscalisationTestConnectionResult
            {
                Connected = false,
                Message = ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    error,
                    "We couldn't check the Fiscalisation API key right now. Please try again.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing the Fiscalisation API key");
            return new FiscalisationTestConnectionResult
            {
                Connected = false,
                Message = ApiErrorResponse.GetFriendlyMessage(
                    ex,
                    "We couldn't check the Fiscalisation API key right now. Please try again.")
            };
        }
    }
}

// Mirrors of the API's DTOs. Nullability has to match the API side exactly — a non-nullable property
// here against a null on the wire throws in System.Text.Json and the page reports no data at all.

public class FiscalisationSettingsResponse
{
    public bool Enabled { get; set; }
    public string? BaseUrl { get; set; }

    /// <summary>Null when no key is configured; never the key itself.</summary>
    public string? ApiKeyMasked { get; set; }

    public bool IsConfigured { get; set; }
    public int DefaultDeviceId { get; set; }
}

public class FiscalisationSettingsUpdateRequest
{
    public string ApiKey { get; set; } = string.Empty;
    public bool TestConnection { get; set; } = true;
}

public class FiscalisationUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>Null when the key was stored without being checked against the platform.</summary>
    public bool? ConnectionTestPassed { get; set; }

    public string? ApiKeyMasked { get; set; }
}

public class FiscalisationUpdateResultResponse
{
    public string? Message { get; set; }
    public bool? ConnectionTestPassed { get; set; }
    public string? ApiKeyMasked { get; set; }
}

public class FiscalisationTestConnectionResult
{
    public bool Connected { get; set; }
    public string? Message { get; set; }
}

public class FiscalisationTestConnectionRequest
{
    /// <summary>Blank tests the key the API is currently running with.</summary>
    public string? ApiKey { get; set; }

    public int? DeviceId { get; set; }
}
