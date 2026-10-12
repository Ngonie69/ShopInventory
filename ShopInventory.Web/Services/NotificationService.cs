using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using System.Net.Http.Json;
using Blazored.LocalStorage;

namespace ShopInventory.Web.Services;

/// <summary>
/// Interface for notification client service
/// </summary>
public interface INotificationClientService
{
    Task<NotificationListResponse?> GetNotificationsAsync(int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null);
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
    private readonly ILocalStorageService _localStorage;
    private readonly CustomAuthStateProvider _authStateProvider;
    private readonly ILogger<NotificationClientService> _logger;

    public NotificationClientService(HttpClient httpClient, ILogger<NotificationClientService> logger, ILocalStorageService localStorage, CustomAuthStateProvider authStateProvider)
    {
        _httpClient = httpClient;
        _localStorage = localStorage;
        _authStateProvider = authStateProvider;
        _logger = logger;
    }

    public async Task<NotificationListResponse?> GetNotificationsAsync(int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null)
    {
        try
        {
            var url = $"api/notification?page={page}&pageSize={pageSize}&unreadOnly={unreadOnly}";
            if (!string.IsNullOrEmpty(category))
                url += $"&category={Uri.EscapeDataString(category)}";
            return await GetAuthenticatedJsonAsync<NotificationListResponse>(url);
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
            var response = await GetAuthenticatedJsonAsync<UnreadCountResponse>("api/notification/unread-count");
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
            var response = await SendAuthenticatedAsync(() => _httpClient.PostAsJsonAsync("api/notification/mark-read", new { NotificationIds = notificationIds }));
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
            var response = await SendAuthenticatedAsync(() => _httpClient.DeleteAsync($"api/notification/{id}"));
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

    /// <summary>
    /// As the signed-in user for as long as the session can be renewed: a valid access token is
    /// attached first, and one the API rejects is renewed and the call sent once more.
    /// </summary>
    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(_httpClient, _authStateProvider, _localStorage, sendAsync, _logger);

    /// <summary>The body deserialized, or the failure thrown, as <c>GetFromJsonAsync</c> has it.</summary>
    private async Task<T?> GetAuthenticatedJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
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
    private readonly ILocalStorageService _localStorage;
    private readonly CustomAuthStateProvider _authStateProvider;
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly ILogger<SyncStatusClientService> _logger;

    private const string ProductsCacheKey = "Products_All";
    private const string PricesCacheKey = "ItemPrices";
    private const string BusinessPartnersCacheKey = "BusinessPartners";
    private const string WarehousesCacheKey = "Warehouses";
    private const string CostCentresCacheKey = "CostCentres";

    public SyncStatusClientService(
        HttpClient httpClient,
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        ILogger<SyncStatusClientService> logger,
        ILocalStorageService localStorage,
        CustomAuthStateProvider authStateProvider)
    {
        _httpClient = httpClient;
        _localStorage = localStorage;
        _authStateProvider = authStateProvider;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<SyncDashboardModel?> GetSyncDashboardAsync()
    {
        try
        {
            var dashboard = await GetAuthenticatedJsonAsync<SyncDashboardModel>("api/sync/status");
            if (dashboard == null)
            {
                return null;
            }

            dashboard.CacheStatuses = await GetCacheSyncStatusAsync();
            return dashboard;
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
            return await GetAuthenticatedJsonAsync<SapConnectionStatusModel>("api/sync/sap-connection");
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
            var response = await SendAuthenticatedAsync(() => _httpClient.PostAsync("api/sync/test-connection", null));
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
            return await GetAuthenticatedJsonAsync<OfflineQueueStatusModel>("api/sync/queue/status");
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
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var syncInfoByKey = await db.CacheSyncInfo
                .AsNoTracking()
                .Where(info => info.CacheKey == ProductsCacheKey
                    || info.CacheKey == PricesCacheKey
                    || info.CacheKey == BusinessPartnersCacheKey
                    || info.CacheKey == WarehousesCacheKey
                    || info.CacheKey == CostCentresCacheKey)
                .ToDictionaryAsync(info => info.CacheKey);

            var productsCount = await db.CachedProducts
                .AsNoTracking()
                .CountAsync(product => product.IsActive);
            var productsLastSyncedAt = await db.CachedProducts
                .AsNoTracking()
                .Where(product => product.IsActive)
                .OrderByDescending(product => product.LastSyncedAt)
                .Select(product => (DateTime?)product.LastSyncedAt)
                .FirstOrDefaultAsync();

            var pricesCount = await db.CachedPrices
                .AsNoTracking()
                .CountAsync();
            var pricesLastSyncedAt = await db.CachedPrices
                .AsNoTracking()
                .OrderByDescending(price => price.LastSyncedAt)
                .Select(price => (DateTime?)price.LastSyncedAt)
                .FirstOrDefaultAsync();

            var businessPartnersCount = await db.CachedBusinessPartners
                .AsNoTracking()
                .CountAsync(partner => partner.IsActive);
            var businessPartnersLastSyncedAt = await db.CachedBusinessPartners
                .AsNoTracking()
                .Where(partner => partner.IsActive)
                .OrderByDescending(partner => partner.LastSyncedAt)
                .Select(partner => (DateTime?)partner.LastSyncedAt)
                .FirstOrDefaultAsync();

            var warehousesCount = await db.CachedWarehouses
                .AsNoTracking()
                .CountAsync(warehouse => warehouse.IsActive);
            var warehousesLastSyncedAt = await db.CachedWarehouses
                .AsNoTracking()
                .Where(warehouse => warehouse.IsActive)
                .OrderByDescending(warehouse => warehouse.LastSyncedAt)
                .Select(warehouse => (DateTime?)warehouse.LastSyncedAt)
                .FirstOrDefaultAsync();

            var costCentresCount = await db.CachedCostCentres
                .AsNoTracking()
                .CountAsync(costCentre => costCentre.IsActive);
            var costCentresLastSyncedAt = await db.CachedCostCentres
                .AsNoTracking()
                .Where(costCentre => costCentre.IsActive)
                .OrderByDescending(costCentre => costCentre.LastSyncedAt)
                .Select(costCentre => (DateTime?)costCentre.LastSyncedAt)
                .FirstOrDefaultAsync();

            return new List<CacheSyncStatusModel>
            {
                BuildLocalCacheStatus(
                    "Products",
                    productsCount,
                    productsLastSyncedAt,
                    syncInfoByKey.GetValueOrDefault(ProductsCacheKey)),
                BuildLocalCacheStatus(
                    "Prices",
                    pricesCount,
                    pricesLastSyncedAt,
                    syncInfoByKey.GetValueOrDefault(PricesCacheKey)),
                BuildLocalCacheStatus(
                    "Business Partners",
                    businessPartnersCount,
                    businessPartnersLastSyncedAt,
                    syncInfoByKey.GetValueOrDefault(BusinessPartnersCacheKey)),
                BuildLocalCacheStatus(
                    "Warehouses",
                    warehousesCount,
                    warehousesLastSyncedAt,
                    syncInfoByKey.GetValueOrDefault(WarehousesCacheKey)),
                BuildLocalCacheStatus(
                    "Cost Centres",
                    costCentresCount,
                    costCentresLastSyncedAt,
                    syncInfoByKey.GetValueOrDefault(CostCentresCacheKey))
            };
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
            return await GetAuthenticatedJsonAsync<List<QueuedTransactionModel>>("api/sync/queue/items") ?? new();
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
            return await GetAuthenticatedJsonAsync<List<ConnectionLogModel>>($"api/sync/logs?count={count}") ?? new();
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
            var response = await SendAuthenticatedAsync(() => _httpClient.PostAsync($"api/sync/queue/{id}/retry", null));
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
            var response = await SendAuthenticatedAsync(() => _httpClient.PostAsync($"api/sync/queue/{id}/cancel", null));
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
            var response = await SendAuthenticatedAsync(() => _httpClient.PostAsync("api/sync/queue/process", null));
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

    private static CacheSyncStatusModel BuildLocalCacheStatus(
        string displayName,
        int itemCount,
        DateTime? lastSyncedAt,
        CacheSyncInfo? syncInfo)
    {
        var syncFailed = syncInfo?.SyncSuccessful == false;
        var cacheAge = lastSyncedAt.HasValue ? DateTime.UtcNow - lastSyncedAt.Value : (TimeSpan?)null;
        var isStale = cacheAge.HasValue && cacheAge.Value.TotalHours > 2;
        var staleMinutes = isStale && cacheAge.HasValue ? (int)cacheAge.Value.TotalMinutes : 0;

        var status = syncFailed
            ? "Error"
            : !lastSyncedAt.HasValue
                ? "Unknown"
                : isStale
                    ? "Stale"
                    : "Synced";

        return new CacheSyncStatusModel
        {
            CacheKey = displayName.Replace(" ", string.Empty),
            DisplayName = displayName,
            LastSyncedAt = lastSyncedAt,
            ItemCount = itemCount,
            IsStale = isStale,
            StaleMinutes = staleMinutes,
            LastError = syncFailed ? syncInfo?.LastError : null,
            LastErrorAt = syncFailed ? syncInfo?.LastSyncedAt : null,
            Status = status
        };
    }

    /// <summary>
    /// As the signed-in user for as long as the session can be renewed: a valid access token is
    /// attached first, and one the API rejects is renewed and the call sent once more.
    /// </summary>
    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(_httpClient, _authStateProvider, _localStorage, sendAsync, _logger);

    /// <summary>The body deserialized, or the failure thrown, as <c>GetFromJsonAsync</c> has it.</summary>
    private async Task<T?> GetAuthenticatedJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
