using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// Interface for notification client service
/// </summary>
public interface INotificationClientService
{
    Task<NotificationListResponse?> GetNotificationsAsync(int page = 1, int pageSize = 20, bool unreadOnly = false);
    Task<int> GetUnreadCountAsync();
    Task<bool> MarkAsReadAsync(List<int>? notificationIds = null);
    Task<bool> DeleteNotificationAsync(int id);
}

/// <summary>
/// Notification client service implementation
/// </summary>
public class NotificationClientService : INotificationClientService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NotificationClientService> _logger;

    public NotificationClientService(HttpClient httpClient, ILogger<NotificationClientService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<NotificationListResponse?> GetNotificationsAsync(int page = 1, int pageSize = 20, bool unreadOnly = false)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<NotificationListResponse>($"api/notification?page={page}&pageSize={pageSize}&unreadOnly={unreadOnly}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching notifications");
            return null;
        }
    }

    public async Task<int> GetUnreadCountAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<UnreadCountResponse>("api/notification/unread-count");
            return response?.UnreadCount ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching unread count");
            return 0;
        }
    }

    public async Task<bool> MarkAsReadAsync(List<int>? notificationIds = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/notification/mark-read", new { NotificationIds = notificationIds });
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking notifications as read");
            return false;
        }
    }

    public async Task<bool> DeleteNotificationAsync(int id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/notification/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting notification {Id}", id);
            return false;
        }
    }

    private class UnreadCountResponse
    {
        public int UnreadCount { get; set; }
    }
}

/// <summary>
/// Interface for sync status client service
/// </summary>
public interface ISyncStatusClientService
{
    Task<SyncDashboardModel?> GetSyncDashboardAsync();
    Task<SapConnectionStatusModel?> GetSapConnectionStatusAsync();
    Task<bool> TestSapConnectionAsync();
    Task<OfflineQueueStatusModel?> GetOfflineQueueStatusAsync();
    Task<List<CacheSyncStatusModel>> GetCacheSyncStatusAsync();
    Task<List<QueuedTransactionModel>> GetQueuedTransactionsAsync();
    Task<List<ConnectionLogModel>> GetConnectionLogsAsync(int count = 50);
    Task<bool> RetryQueueItemAsync(int id);
    Task<bool> CancelQueueItemAsync(int id);
    Task<int> ProcessQueueAsync();
}

/// <summary>
/// Sync status client service implementation
/// </summary>
public class SyncStatusClientService : ISyncStatusClientService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SyncStatusClientService> _logger;

    public SyncStatusClientService(HttpClient httpClient, ILogger<SyncStatusClientService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SyncDashboardModel?> GetSyncDashboardAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SyncDashboardModel>("api/sync/status");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sync dashboard");
            return null;
        }
    }

    public async Task<SapConnectionStatusModel?> GetSapConnectionStatusAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SapConnectionStatusModel>("api/sync/sap-connection");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking SAP connection");
            return null;
        }
    }

    public async Task<bool> TestSapConnectionAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("api/sync/test-connection", null);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ConnectionTestResult>();
                return result?.IsConnected ?? false;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing SAP connection");
            return false;
        }
    }

    public async Task<OfflineQueueStatusModel?> GetOfflineQueueStatusAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<OfflineQueueStatusModel>("api/sync/queue/status");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching queue status");
            return null;
        }
    }

    public async Task<List<CacheSyncStatusModel>> GetCacheSyncStatusAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<CacheSyncStatusModel>>("api/sync/cache-status") ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching cache status");
            return new();
        }
    }

    public async Task<List<QueuedTransactionModel>> GetQueuedTransactionsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<QueuedTransactionModel>>("api/sync/queue/items") ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching queued transactions");
            return new();
        }
    }

    public async Task<List<ConnectionLogModel>> GetConnectionLogsAsync(int count = 50)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<ConnectionLogModel>>($"api/sync/logs?count={count}") ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching connection logs");
            return new();
        }
    }

    public async Task<bool> RetryQueueItemAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/sync/queue/{id}/retry", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying queue item {Id}", id);
            return false;
        }
    }

    public async Task<bool> CancelQueueItemAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/sync/queue/{id}/cancel", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling queue item {Id}", id);
            return false;
        }
    }

    public async Task<int> ProcessQueueAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("api/sync/queue/process", null);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ProcessQueueResult>();
                return result?.ProcessedCount ?? 0;
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing queue");
            return 0;
        }
    }

    private class ConnectionTestResult
    {
        public bool IsConnected { get; set; }
    }

    private class ProcessQueueResult
    {
        public int ProcessedCount { get; set; }
    }
}
