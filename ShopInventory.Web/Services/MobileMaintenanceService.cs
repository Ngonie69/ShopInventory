using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// The mobile maintenance lockout, as the settings screen reads and writes it.
/// </summary>
public interface IMobileMaintenanceService
{
    Task<MobileMaintenanceSettingsResponse?> GetSettingsAsync();
    Task<MobileMaintenanceUpdateResult> SetAsync(SetMobileMaintenanceApiRequest request);
}

public class MobileMaintenanceService(
    HttpClient httpClient,
    ILogger<MobileMaintenanceService> logger) : IMobileMaintenanceService
{
    public async Task<MobileMaintenanceSettingsResponse?> GetSettingsAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<MobileMaintenanceSettingsResponse>("api/maintenance/mobile");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the mobile maintenance settings");
            return null;
        }
    }

    public async Task<MobileMaintenanceUpdateResult> SetAsync(SetMobileMaintenanceApiRequest request)
    {
        try
        {
            var response = await httpClient.PutAsJsonAsync("api/maintenance/mobile", request);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SetMobileMaintenanceApiResponse>();
                return new MobileMaintenanceUpdateResult
                {
                    Success = true,
                    Message = result?.Message ?? "Maintenance mode updated.",
                    Settings = result?.Settings
                };
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            var message = ExtractErrorMessage(errorBody, response.StatusCode);
            logger.LogWarning(
                "Failed to set the mobile maintenance lockout: {StatusCode} - {Message}",
                response.StatusCode,
                message);

            return new MobileMaintenanceUpdateResult { Success = false, Message = message };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting the mobile maintenance lockout");
            return new MobileMaintenanceUpdateResult
            {
                Success = false,
                Message = ApiErrorResponse.GetFriendlyMessage(
                    ex,
                    "We couldn't change maintenance mode right now. Please try again.")
            };
        }
    }

    /// <summary>
    /// Pulls the sentence out of an RFC 9457 problem document.
    /// </summary>
    /// <remarks>
    /// Same reader as <c>MobileVersionPolicySettingsService</c>: the validation dictionary first,
    /// then <c>title</c>, then <c>message</c>. Worth keeping identical rather than clever, because
    /// what the operator needs here is the API's own sentence, not a status code.
    /// </remarks>
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

/// <summary>
/// Mirrors the API's <c>MobileMaintenanceSettingsDto</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, like every other DTO on this side. The nullability has to match the API's or a
/// field arrives null and the page renders an empty control over a setting that is in fact set.
/// </remarks>
public class MobileMaintenanceSettingsResponse
{
    public bool Enabled { get; set; }
    public bool IsActive { get; set; }
    public string Scope { get; set; } = "Transactions";
    public string Message { get; set; } = string.Empty;
    public string DefaultMessage { get; set; } = string.Empty;
    public List<string> AppIds { get; set; } = [];
    public List<MobileMaintenanceAppResponse> CoveredApps { get; set; } = [];
    public List<MobileMaintenanceAppResponse> AvailableApps { get; set; } = [];
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public class MobileMaintenanceAppResponse
{
    public string AppId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class SetMobileMaintenanceApiRequest
{
    public bool Enabled { get; set; }
    public string? Scope { get; set; }
    public string? Message { get; set; }
    public List<string>? AppIds { get; set; }
    public DateTime? EndsAtUtc { get; set; }
}

public class SetMobileMaintenanceApiResponse
{
    public string? Message { get; set; }
    public MobileMaintenanceSettingsResponse? Settings { get; set; }
}

public class MobileMaintenanceUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public MobileMaintenanceSettingsResponse? Settings { get; set; }
}
