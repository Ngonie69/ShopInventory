using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

public interface IWarehouseStockCacheService
{
    /// <summary>
    /// Gets cached stock for a warehouse. Returns cached data immediately if available,
    /// and triggers background sync if cache is stale.
    /// </summary>
    Task<WarehouseProductsPagedResponse?> GetCachedStockAsync(string warehouseCode, int page = 1, int pageSize = 20);

    /// <summary>
    /// Forces a full sync of stock data for a warehouse
    /// </summary>
    Task<bool> SyncWarehouseStockAsync(string warehouseCode);

    /// <summary>
    /// Gets the sync status for a warehouse
    /// </summary>
    Task<CacheSyncInfo?> GetSyncStatusAsync(string warehouseCode);

    /// <summary>
    /// Event raised when background sync completes
    /// </summary>
    event EventHandler<string>? SyncCompleted;
}

public class WarehouseStockCacheService : IWarehouseStockCacheService
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<WarehouseStockCacheService> _logger;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> _syncInProgress = new();
    private static readonly object _syncLock = new();

    public event EventHandler<string>? SyncCompleted;

    public WarehouseStockCacheService(
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        HttpClient httpClient,
        ILogger<WarehouseStockCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<WarehouseProductsPagedResponse?> GetCachedStockAsync(string warehouseCode, int page = 1, int pageSize = 20)
    {
        _logger.LogInformation("GetCachedStockAsync called for warehouse {WarehouseCode}, page {Page}, pageSize {PageSize}", warehouseCode, page, pageSize);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        _logger.LogDebug("DbContext created successfully");

        var cacheKey = $"WarehouseStock_{warehouseCode}";

        try
        {
            var syncInfo = await dbContext.CacheSyncInfo.FindAsync(cacheKey);
            _logger.LogDebug("CacheSyncInfo lookup completed. Found: {Found}", syncInfo != null);

            var isCacheStale = syncInfo == null ||
                          (DateTime.UtcNow - syncInfo.LastSyncedAt) > _cacheExpiration ||
                          !syncInfo.SyncSuccessful;

            // Get cached data
            _logger.LogDebug("Attempting to query CachedWarehouseStocks table for warehouse {WarehouseCode}", warehouseCode);
            var cachedCount = await dbContext.CachedWarehouseStocks
                .Where(s => s.WarehouseCode == warehouseCode)
                .CountAsync();
            _logger.LogDebug("CachedWarehouseStocks count query completed. Count: {Count}", cachedCount);

            if (cachedCount > 0)
            {
                // Return cached data
                var skip = (page - 1) * pageSize;
                var cachedItems = await dbContext.CachedWarehouseStocks
                    .Where(s => s.WarehouseCode == warehouseCode)
                    .OrderBy(s => s.ItemCode)
                    .Skip(skip)
                    .Take(pageSize)
                    .ToListAsync();

                var response = new WarehouseProductsPagedResponse
                {
                    WarehouseCode = warehouseCode,
                    Page = page,
                    PageSize = pageSize,
                    Count = cachedItems.Count,
                    HasMore = skip + cachedItems.Count < cachedCount,
                    Products = cachedItems.Select(MapCachedToProduct).ToList()
                };

                // Trigger background sync if cache is stale
                if (isCacheStale)
                {
                    _ = Task.Run(async () => await SyncWarehouseStockInBackgroundAsync(warehouseCode));
                }

                return response;
            }
        }
        catch (Npgsql.PostgresException pgEx)
        {
            _logger.LogError(pgEx, "PostgreSQL error querying CachedWarehouseStocks. SqlState: {SqlState}, Message: {Message}, Position: {Position}",
                pgEx.SqlState, pgEx.MessageText, pgEx.Position);
            _logger.LogError("Full PostgreSQL exception details: {Details}", pgEx.ToString());
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying cache tables: {Message}", ex.Message);
            throw;
        }

        // No cached data - fetch first page from API and start background sync
        _logger.LogInformation("No cached stock for warehouse {WarehouseCode}, fetching from API", warehouseCode);

        try
        {
            // Fetch first page immediately
            var apiResponse = await FetchStockFromApiAsync(warehouseCode, page, pageSize);
            if (apiResponse != null && apiResponse.Items?.Any() == true)
            {
                // Save first page to cache
                await SaveStockToCacheAsync(warehouseCode, apiResponse.Items);

                // Start background sync for remaining items
                _ = Task.Run(async () => await SyncRemainingStockInBackgroundAsync(warehouseCode, apiResponse.HasMore));

                return new WarehouseProductsPagedResponse
                {
                    WarehouseCode = apiResponse.WarehouseCode,
                    Page = apiResponse.Page,
                    PageSize = apiResponse.PageSize,
                    Count = apiResponse.Count,
                    HasMore = apiResponse.HasMore,
                    Products = apiResponse.Items.Select(MapStockToProduct).ToList()
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching stock from API for warehouse {WarehouseCode}", warehouseCode);
        }

        return null;
    }

    public async Task<bool> SyncWarehouseStockAsync(string warehouseCode)
    {
        return await SyncWarehouseStockInBackgroundAsync(warehouseCode);
    }

    public async Task<CacheSyncInfo?> GetSyncStatusAsync(string warehouseCode)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var cacheKey = $"WarehouseStock_{warehouseCode}";
        return await dbContext.CacheSyncInfo.FindAsync(cacheKey);
    }

    private async Task<bool> SyncWarehouseStockInBackgroundAsync(string warehouseCode)
    {
        // Prevent concurrent syncs for the same warehouse
        lock (_syncLock)
        {
            if (_syncInProgress.Contains(warehouseCode))
            {
                _logger.LogDebug("Sync already in progress for warehouse {WarehouseCode}", warehouseCode);
                return false;
            }
            _syncInProgress.Add(warehouseCode);
        }

        try
        {
            _logger.LogInformation("Starting full stock sync for warehouse {WarehouseCode}", warehouseCode);

            var allItems = new List<StockItemDto>();
            var page = 1;
            var pageSize = 100;
            var hasMore = true;

            while (hasMore)
            {
                var response = await FetchStockFromApiAsync(warehouseCode, page, pageSize);
                if (response?.Items != null)
                {
                    allItems.AddRange(response.Items);
                    hasMore = response.HasMore;
                    page++;
                }
                else
                {
                    hasMore = false;
                }
            }

            if (allItems.Any())
            {
                await ReplaceWarehouseStockCacheAsync(warehouseCode, allItems);
                await UpdateSyncInfoAsync(warehouseCode, allItems.Count, true, null);
                _logger.LogInformation("Completed stock sync for warehouse {WarehouseCode}: {Count} items", warehouseCode, allItems.Count);
                SyncCompleted?.Invoke(this, warehouseCode);
                return true;
            }
            else
            {
                await UpdateSyncInfoAsync(warehouseCode, 0, true, "No items found");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during stock sync for warehouse {WarehouseCode}", warehouseCode);
            await UpdateSyncInfoAsync(warehouseCode, 0, false, ex.Message);
            return false;
        }
        finally
        {
            lock (_syncLock)
            {
                _syncInProgress.Remove(warehouseCode);
            }
        }
    }

    private async Task SyncRemainingStockInBackgroundAsync(string warehouseCode, bool hasMore)
    {
        if (!hasMore) return;

        // Prevent concurrent syncs for the same warehouse
        lock (_syncLock)
        {
            if (_syncInProgress.Contains(warehouseCode))
            {
                _logger.LogDebug("Sync already in progress for warehouse {WarehouseCode}", warehouseCode);
                return;
            }
            _syncInProgress.Add(warehouseCode);
        }

        try
        {
            _logger.LogInformation("Starting background sync for remaining stock in warehouse {WarehouseCode}", warehouseCode);

            var page = 2; // Start from page 2 since page 1 is already cached
            var pageSize = 100;

            while (hasMore)
            {
                var response = await FetchStockFromApiAsync(warehouseCode, page, pageSize);
                if (response?.Items != null && response.Items.Any())
                {
                    await SaveStockToCacheAsync(warehouseCode, response.Items);
                    hasMore = response.HasMore;
                    page++;
                    _logger.LogDebug("Cached page {Page} for warehouse {WarehouseCode}: {Count} items",
                        page - 1, warehouseCode, response.Items.Count);
                }
                else
                {
                    hasMore = false;
                }
            }

            // Update sync info
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var totalCount = await dbContext.CachedWarehouseStocks
                .Where(s => s.WarehouseCode == warehouseCode)
                .CountAsync();

            await UpdateSyncInfoAsync(warehouseCode, totalCount, true, null);
            _logger.LogInformation("Completed background sync for warehouse {WarehouseCode}: {Count} total items", warehouseCode, totalCount);
            SyncCompleted?.Invoke(this, warehouseCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during background sync for warehouse {WarehouseCode}", warehouseCode);
            await UpdateSyncInfoAsync(warehouseCode, 0, false, ex.Message);
        }
        finally
        {
            lock (_syncLock)
            {
                _syncInProgress.Remove(warehouseCode);
            }
        }
    }

    private async Task<StockPagedApiResponse?> FetchStockFromApiAsync(string warehouseCode, int page, int pageSize)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<StockPagedApiResponse>(
                $"api/stock/warehouse/{warehouseCode}/paged?page={page}&pageSize={pageSize}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching stock from API for warehouse {WarehouseCode}, page {Page}", warehouseCode, page);
            return null;
        }
    }

    private async Task SaveStockToCacheAsync(string warehouseCode, List<StockItemDto> items)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        foreach (var item in items)
        {
            var existing = await dbContext.CachedWarehouseStocks
                .FirstOrDefaultAsync(s => s.WarehouseCode == warehouseCode && s.ItemCode == item.ItemCode);

            if (existing != null)
            {
                existing.ItemName = item.ItemName;
                existing.BarCode = item.BarCode;
                existing.InStock = item.InStock;
                existing.Committed = item.Committed;
                existing.Ordered = item.Ordered;
                existing.Available = item.Available;
                existing.UoM = item.UoM;
                existing.LastSyncedAt = now;
            }
            else
            {
                dbContext.CachedWarehouseStocks.Add(new CachedWarehouseStock
                {
                    ItemCode = item.ItemCode ?? string.Empty,
                    ItemName = item.ItemName,
                    BarCode = item.BarCode,
                    WarehouseCode = warehouseCode,
                    InStock = item.InStock,
                    Committed = item.Committed,
                    Ordered = item.Ordered,
                    Available = item.Available,
                    UoM = item.UoM,
                    LastSyncedAt = now
                });
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task ReplaceWarehouseStockCacheAsync(string warehouseCode, List<StockItemDto> items)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Delete existing stock for this warehouse
        await dbContext.CachedWarehouseStocks
            .Where(s => s.WarehouseCode == warehouseCode)
            .ExecuteDeleteAsync();

        // Add all new items
        var now = DateTime.UtcNow;
        var newItems = items.Select(item => new CachedWarehouseStock
        {
            ItemCode = item.ItemCode ?? string.Empty,
            ItemName = item.ItemName,
            BarCode = item.BarCode,
            WarehouseCode = warehouseCode,
            InStock = item.InStock,
            Committed = item.Committed,
            Ordered = item.Ordered,
            Available = item.Available,
            UoM = item.UoM,
            LastSyncedAt = now
        }).ToList();

        dbContext.CachedWarehouseStocks.AddRange(newItems);
        await dbContext.SaveChangesAsync();
    }

    private async Task UpdateSyncInfoAsync(string warehouseCode, int itemCount, bool success, string? error)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var cacheKey = $"WarehouseStock_{warehouseCode}";

        var syncInfo = await dbContext.CacheSyncInfo.FindAsync(cacheKey);
        if (syncInfo == null)
        {
            syncInfo = new CacheSyncInfo { CacheKey = cacheKey };
            dbContext.CacheSyncInfo.Add(syncInfo);
        }

        syncInfo.LastSyncedAt = DateTime.UtcNow;
        syncInfo.ItemCount = itemCount;
        syncInfo.SyncSuccessful = success;
        syncInfo.LastError = error;

        await dbContext.SaveChangesAsync();
    }

    private static ProductDto MapCachedToProduct(CachedWarehouseStock cached)
    {
        return new ProductDto
        {
            ItemCode = cached.ItemCode,
            ItemName = cached.ItemName,
            BarCode = cached.BarCode,
            QuantityInStock = cached.InStock,
            QuantityAvailable = cached.Available,
            QuantityCommitted = cached.Committed,
            QuantityOnStock = cached.InStock,
            UoM = cached.UoM,
            ManagesBatches = false,
            Batches = null
        };
    }

    private static ProductDto MapStockToProduct(StockItemDto stock)
    {
        return new ProductDto
        {
            ItemCode = stock.ItemCode,
            ItemName = stock.ItemName,
            BarCode = stock.BarCode,
            QuantityInStock = stock.InStock,
            QuantityAvailable = stock.Available,
            QuantityCommitted = stock.Committed,
            QuantityOnStock = stock.InStock,
            UoM = stock.UoM,
            ManagesBatches = false,
            Batches = null
        };
    }
}

// DTOs for stock API responses (internal to this service)
internal class StockPagedApiResponse
{
    public string? WarehouseCode { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public DateTime QueryDate { get; set; }
    public List<StockItemDto>? Items { get; set; }
}

internal class StockItemDto
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? BarCode { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal InStock { get; set; }
    public decimal Committed { get; set; }
    public decimal Ordered { get; set; }
    public decimal Available { get; set; }
    public string? UoM { get; set; }
}
