using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IWebhookService
{
    Task<WebhookListResponse?> GetAllWebhooksAsync();
    Task<WebhookDto?> GetWebhookByIdAsync(int id);
    Task<WebhookDto?> CreateWebhookAsync(CreateWebhookRequest request);
    Task<WebhookDto?> UpdateWebhookAsync(int id, UpdateWebhookRequest request);
    Task<bool> DeleteWebhookAsync(int id);
    Task<bool> ToggleWebhookAsync(int id, bool isActive);
    Task<WebhookDeliveryListResponse?> GetDeliveriesAsync(int webhookId, int page = 1, int pageSize = 20);
    Task<bool> TestWebhookAsync(int id);
    Task<List<string>> GetAvailableEventsAsync();
}

public class WebhookService : IWebhookService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(HttpClient httpClient, ILogger<WebhookService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<WebhookListResponse?> GetAllWebhooksAsync()
    {
        try
        {
            var webhooks = await _httpClient.GetFromJsonAsync<List<WebhookDto>>("api/webhook");
            return webhooks != null ? new WebhookListResponse { Webhooks = webhooks, TotalCount = webhooks.Count } : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching webhooks");
            return null;
        }
    }

    public async Task<WebhookDto?> GetWebhookByIdAsync(int id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<WebhookDto>($"api/webhook/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching webhook {Id}", id);
            return null;
        }
    }

    public async Task<WebhookDto?> CreateWebhookAsync(CreateWebhookRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/webhook", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<WebhookDto>();
            }
            _logger.LogWarning("Failed to create webhook: {StatusCode}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating webhook");
            return null;
        }
    }

    public async Task<WebhookDto?> UpdateWebhookAsync(int id, UpdateWebhookRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/webhook/{id}", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<WebhookDto>();
            }
            _logger.LogWarning("Failed to update webhook {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating webhook {Id}", id);
            return null;
        }
    }

    public async Task<bool> DeleteWebhookAsync(int id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/webhook/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting webhook {Id}", id);
            return false;
        }
    }

    public async Task<bool> ToggleWebhookAsync(int id, bool isActive)
    {
        try
        {
            var request = new UpdateWebhookRequest { IsActive = isActive };
            var response = await _httpClient.PutAsJsonAsync($"api/webhook/{id}", request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling webhook {Id}", id);
            return false;
        }
    }

    public async Task<WebhookDeliveryListResponse?> GetDeliveriesAsync(int webhookId, int page = 1, int pageSize = 20)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<WebhookDeliveryListResponse>($"api/webhook/{webhookId}/deliveries?page={page}&pageSize={pageSize}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching webhook deliveries for {WebhookId}", webhookId);
            return null;
        }
    }

    public async Task<bool> TestWebhookAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/webhook/{id}/test", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing webhook {Id}", id);
            return false;
        }
    }

    public async Task<List<string>> GetAvailableEventsAsync()
    {
        try
        {
            var events = await _httpClient.GetFromJsonAsync<List<string>>("api/webhook/events");
            return events ?? WebhookEvents.All;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching available webhook events");
            return WebhookEvents.All;
        }
    }
}
