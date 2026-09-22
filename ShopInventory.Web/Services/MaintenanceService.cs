using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// The maintenance lockout, as the settings screen reads and writes it and the banner reads it.
/// </summary>
public interface IMaintenanceService
{
    Task<MaintenanceSettingsResponse?> GetSettingsAsync();
    Task<MaintenanceUpdateResult> SetAsync(SetMaintenanceApiRequest request);

    /// <summary>
    /// Whether the portal itself is frozen right now, for the banner every page shows.
    /// </summary>
    /// <remarks>
    /// Anonymous and exempt from the lockout, so it answers whether or not the caller would be
    /// refused — and it answers for the web portal audience, because the Web's clients name
    /// themselves. A lockout aimed only at the phones therefore puts no banner on the office's
    /// screens.
    /// </remarks>
    Task<MaintenanceStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default);
}

public class MaintenanceService(
    HttpClient httpClient,
    ILogger<MaintenanceService> logger) : IMaintenanceService
{
    public async Task<MaintenanceSettingsResponse?> GetSettingsAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<MaintenanceSettingsResponse>("api/maintenance/mobile");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the maintenance settings");
            return null;
        }
    }

    /// <summary>
    /// How long the banner will wait for an answer.
    /// </summary>
    /// <remarks>
    /// Its own budget, well short of the shared client's five minutes. This call is made while the
    /// layout renders, and the one situation it exists for is the one where the API may be hanging
    /// rather than refusing — so inheriting that timeout would mean a lockout could leave every
    /// page in the portal blank for five minutes, which is a worse outage than the maintenance.
    /// </remarks>
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(5);

    public async Task<MaintenanceStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(StatusTimeout);

            return await httpClient.GetFromJsonAsync<MaintenanceStatusResponse>(
                "api/maintenance/status", budget.Token);
        }
        catch (Exception ex)
        {
            // Swallowed to a null rather than surfaced. This drives a banner, and a banner that
            // could not be fetched must not become an error on a page that is otherwise fine —
            // least of all during maintenance, when the API is exactly what may be unreachable.
            logger.LogDebug(ex, "Could not read the maintenance status");
            return null;
        }
    }

    public async Task<MaintenanceUpdateResult> SetAsync(SetMaintenanceApiRequest request)
    {
        try
        {
            var response = await httpClient.PutAsJsonAsync("api/maintenance/mobile", request);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SetMaintenanceApiResponse>();
                return new MaintenanceUpdateResult
                {
                    Success = true,
                    Message = result?.Message ?? "Maintenance mode updated.",
                    Settings = result?.Settings
                };
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            var message = ExtractErrorMessage(errorBody, response.StatusCode);
            logger.LogWarning(
                "Failed to set the maintenance lockout: {StatusCode} - {Message}",
                response.StatusCode,
                message);

            return new MaintenanceUpdateResult { Success = false, Message = message };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting the maintenance lockout");
            return new MaintenanceUpdateResult
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
/// Mirrors the API's <c>MaintenanceSettingsDto</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, like every other DTO on this side. The nullability has to match the API's or a
/// field arrives null and the page renders an empty control over a setting that is in fact set.
/// </remarks>
public class MaintenanceSettingsResponse
{
    public bool Enabled { get; set; }
    public bool IsActive { get; set; }
    public string Scope { get; set; } = "Transactions";
    public List<string> Audiences { get; set; } = [];
    public List<MaintenanceAudienceResponse> AvailableAudiences { get; set; } = [];
    public string Message { get; set; } = string.Empty;
    public string DefaultMessage { get; set; } = string.Empty;
    public List<string> AppIds { get; set; } = [];
    public List<MaintenanceAppResponse> CoveredApps { get; set; } = [];
    public List<MaintenanceAppResponse> AvailableApps { get; set; } = [];
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public class MaintenanceAppResponse
{
    public string AppId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class MaintenanceAudienceResponse
{
    public string Audience { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Mirrors the API's <c>MaintenanceStatusDto</c>.
/// </summary>
public class MaintenanceStatusResponse
{
    public bool IsActive { get; set; }
    public string Audience { get; set; } = string.Empty;
    public string Scope { get; set; } = "Transactions";
    public string Message { get; set; } = string.Empty;
    public bool ReadsAllowed { get; set; } = true;
    public DateTime? EndsAtUtc { get; set; }
    public DateTime CheckedAtUtc { get; set; }
}

public class SetMaintenanceApiRequest
{
    public bool Enabled { get; set; }
    public string? Scope { get; set; }
    public List<string>? Audiences { get; set; }
    public string? Message { get; set; }
    public List<string>? AppIds { get; set; }
    public DateTime? EndsAtUtc { get; set; }
}

public class SetMaintenanceApiResponse
{
    public string? Message { get; set; }
    public MaintenanceSettingsResponse? Settings { get; set; }
}

public class MaintenanceUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public MaintenanceSettingsResponse? Settings { get; set; }
}
