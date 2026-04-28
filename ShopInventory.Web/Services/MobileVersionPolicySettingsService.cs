using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

public interface IMobileVersionPolicySettingsService
{
    Task<MobileVersionPolicySettingsResponse?> GetSettingsAsync();
    Task<MobileVersionPolicySettingsUpdateResult> UpdateSettingsAsync(MobileVersionPolicySettingsUpdateRequest request);
}

public class MobileVersionPolicySettingsService : IMobileVersionPolicySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<MobileVersionPolicySettingsService> _logger;

    public MobileVersionPolicySettingsService(HttpClient httpClient, ILogger<MobileVersionPolicySettingsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<MobileVersionPolicySettingsResponse?> GetSettingsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<MobileVersionPolicySettingsResponse>("api/appversion/mobile/settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching mobile version policy settings");
            return null;
        }
    }

    public async Task<MobileVersionPolicySettingsUpdateResult> UpdateSettingsAsync(MobileVersionPolicySettingsUpdateRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync("api/appversion/mobile/settings", request);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<MobileVersionPolicySettingsUpdateResultResponse>();
                return new MobileVersionPolicySettingsUpdateResult
                {
                    Success = true,
                    Message = result?.Message ?? "Mobile app policy updated successfully."
                };
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            var message = ExtractErrorMessage(errorBody, response.StatusCode);
            _logger.LogWarning(
                "Failed to update mobile version policy settings: {StatusCode} - {Message}",
                response.StatusCode,
                message);

            return new MobileVersionPolicySettingsUpdateResult
            {
                Success = false,
                Message = message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating mobile version policy settings");
            return new MobileVersionPolicySettingsUpdateResult
            {
                Success = false,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    private static string ExtractErrorMessage(string responseBody, HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return $"Failed to update: HTTP {(int)statusCode}";

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = new List<string>();
                foreach (var property in errors.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            messages.Add(item.GetString()!);
                    }
                }

                if (messages.Count > 0)
                    return string.Join(" ", messages.Distinct(StringComparer.Ordinal));
            }

            if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString() ?? $"Failed to update: HTTP {(int)statusCode}";

            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? $"Failed to update: HTTP {(int)statusCode}";
        }
        catch
        {
        }

        return responseBody;
    }
}

public class MobileVersionPolicySettingsResponse
{
    public bool Enabled { get; set; }
    public bool RequireHeaders { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string RecommendedVersion { get; set; } = string.Empty;
    public string MinimumSupportedVersion { get; set; } = string.Empty;
    public string GooglePlayUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string WarnMessage { get; set; } = string.Empty;
    public string BlockMessage { get; set; } = string.Empty;
}

public class MobileVersionPolicySettingsUpdateRequest
{
    public bool Enabled { get; set; }
    public bool RequireHeaders { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string RecommendedVersion { get; set; } = string.Empty;
    public string MinimumSupportedVersion { get; set; } = string.Empty;
    public string GooglePlayUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string WarnMessage { get; set; } = string.Empty;
    public string BlockMessage { get; set; } = string.Empty;
}

public class MobileVersionPolicySettingsUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class MobileVersionPolicySettingsUpdateResultResponse
{
    public string? Message { get; set; }
}