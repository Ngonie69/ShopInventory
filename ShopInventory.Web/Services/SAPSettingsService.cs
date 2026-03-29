using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface ISAPSettingsService
{
    Task<SAPSettingsResponse?> GetSettingsAsync();
    Task<SAPUpdateResult> UpdateSettingsAsync(SAPSettingsUpdateRequest request);
    Task<SAPTestConnectionResult> TestConnectionAsync();
}

public class SAPSettingsService : ISAPSettingsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SAPSettingsService> _logger;

    public SAPSettingsService(HttpClient httpClient, ILogger<SAPSettingsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SAPSettingsResponse?> GetSettingsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SAPSettingsResponse>("api/sap-settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching SAP settings");
            return null;
        }
    }

    public async Task<SAPUpdateResult> UpdateSettingsAsync(SAPSettingsUpdateRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync("api/sap-settings", request);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SAPUpdateResultResponse>();
                return new SAPUpdateResult
                {
                    Success = true,
                    Message = result?.Message ?? "Settings updated successfully.",
                    ConnectionTestPassed = result?.ConnectionTestPassed
                };
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to update SAP settings: {StatusCode} - {Error}", response.StatusCode, error);
            return new SAPUpdateResult { Success = false, Message = $"Failed to update: {response.StatusCode}" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating SAP settings");
            return new SAPUpdateResult { Success = false, Message = $"Error: {ex.Message}" };
        }
    }

    public async Task<SAPTestConnectionResult> TestConnectionAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("api/sap-settings/test-connection", null);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SAPTestConnectionResult>();
                return result ?? new SAPTestConnectionResult { Connected = false, Message = "No response" };
            }

            return new SAPTestConnectionResult { Connected = false, Message = $"HTTP {response.StatusCode}" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing SAP connection");
            return new SAPTestConnectionResult { Connected = false, Message = ex.Message };
        }
    }
}

public class SAPSettingsResponse
{
    public string? ServiceLayerUrl { get; set; }
    public string? CompanyDB { get; set; }
    public string? UserName { get; set; }
    public bool IsConfigured { get; set; }
}

public class SAPSettingsUpdateRequest
{
    public string ServiceLayerUrl { get; set; } = string.Empty;
    public string CompanyDB { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool TestConnection { get; set; } = true;
}

public class SAPUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool? ConnectionTestPassed { get; set; }
}

public class SAPUpdateResultResponse
{
    public string? Message { get; set; }
    public bool? ConnectionTestPassed { get; set; }
}

public class SAPTestConnectionResult
{
    public bool Connected { get; set; }
    public string? Message { get; set; }
}
