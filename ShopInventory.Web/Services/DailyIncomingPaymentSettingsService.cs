using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// The API's switch between desktop sales posting invoices only, and invoices plus one incoming
/// payment per customer per day.
/// </summary>
public interface IDailyIncomingPaymentSettingsService
{
    Task<DailyIncomingPaymentSettings?> GetAsync();
    Task<DailyIncomingPaymentSettingsResult> SetEnabledAsync(bool enabled);
}

public class DailyIncomingPaymentSettingsService(
    HttpClient httpClient,
    ILogger<DailyIncomingPaymentSettingsService> logger) : IDailyIncomingPaymentSettingsService
{
    private const string Route = "api/daily-incoming-payment-settings";

    public async Task<DailyIncomingPaymentSettings?> GetAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<DailyIncomingPaymentSettings>(Route);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the daily incoming payment switch");
            return null;
        }
    }

    public async Task<DailyIncomingPaymentSettingsResult> SetEnabledAsync(bool enabled)
    {
        const string fallback = "We couldn't change the daily incoming payment setting right now. Please try again.";

        try
        {
            var response = await httpClient.PutAsJsonAsync(Route, new { enabled });
            if (response.IsSuccessStatusCode)
            {
                return new DailyIncomingPaymentSettingsResult(
                    true, await response.Content.ReadFromJsonAsync<DailyIncomingPaymentSettings>(), null);
            }

            var error = await response.Content.ReadAsStringAsync();
            logger.LogWarning(
                "Failed to set the daily incoming payment switch: {StatusCode} - {Error}", response.StatusCode, error);
            return new DailyIncomingPaymentSettingsResult(
                false, null, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, error, fallback));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting the daily incoming payment switch");
            return new DailyIncomingPaymentSettingsResult(false, null, ApiErrorResponse.GetFriendlyMessage(ex, fallback));
        }
    }
}

public class DailyIncomingPaymentSettings
{
    public bool Enabled { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed record DailyIncomingPaymentSettingsResult(bool Success, DailyIncomingPaymentSettings? Settings, string? Error);
