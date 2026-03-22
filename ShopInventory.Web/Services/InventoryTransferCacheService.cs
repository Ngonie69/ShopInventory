using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

public interface IInventoryTransferCacheService
{
    /// <summary>
    /// Gets cached transfers for a warehouse with pagination. Returns cached data immediately if available,
    /// and triggers background sync if cache is stale.
    /// </summary>
    Task<InventoryTransferListResponse?> GetCachedTransfersAsync(string warehouseCode, int page = 1, int pageSize = 20);

    /// <summary>
    /// Gets cached transfers by date range for a warehouse
    /// </summary>
    Task<InventoryTransferDateResponse?> GetCachedTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate);

    /// <summary>
    /// Gets a single transfer by DocEntry
    /// </summary>
    Task<InventoryTransferDto?> GetCachedTransferByDocEntryAsync(int docEntry);

    /// <summary>
    /// Forces a full sync of transfer data for a warehouse
    /// </summary>
    Task<bool> SyncTransfersAsync(string warehouseCode);

    /// <summary>
    /// Gets the sync status for a warehouse
    /// </summary>
    Task<CacheSyncInfo?> GetSyncStatusAsync(string warehouseCode);

    /// <summary>
    /// Event raised when background sync completes
    /// </summary>
    event EventHandler<string>? SyncCompleted;
}

public class InventoryTransferCacheService : IInventoryTransferCacheService
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<InventoryTransferCacheService> _logger;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> _syncInProgress = new();
    private static readonly object _syncLock = new();

    public event EventHandler<string>? SyncCompleted;

    public InventoryTransferCacheService(
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        HttpClient httpClient,
        ILogger<InventoryTransferCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<InventoryTransferListResponse?> GetCachedTransfersAsync(string warehouseCode, int page = 1, int pageSize = 20)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var cacheKey = $"InventoryTransfers_{warehouseCode}";
        var syncInfo = await dbContext.CacheSyncInfo.FindAsync(cacheKey);
        var isCacheStale = syncInfo == null ||
                          (DateTime.UtcNow - syncInfo.LastSyncedAt) > _cacheExpiration ||
                          !syncInfo.SyncSuccessful;

        // Get cached count - transfers where the warehouse is either from or to
        var cachedCount = await dbContext.CachedInventoryTransfers
            .Where(t => t.FromWarehouse == warehouseCode || t.ToWarehouse == warehouseCode)
            .CountAsync();

        if (cachedCount > 0)
        {
            // Return cached data
            var skip = (page - 1) * pageSize;
            var cachedItems = await dbContext.CachedInventoryTransfers
                .Where(t => t.FromWarehouse == warehouseCode || t.ToWarehouse == warehouseCode)
                .OrderByDescending(t => t.DocDate)
                .ThenByDescending(t => t.DocNum)
                .Skip(skip)
                .Take(pageSize)
                .ToListAsync();

            var response = new InventoryTransferListResponse
            {
                Warehouse = warehouseCode,
                Page = page,
                PageSize = pageSize,
                Count = cachedCount,
                HasMore = skip + cachedItems.Count < cachedCount,
                Transfers = cachedItems.Select(MapCachedToDto).ToList()
            };

            // Trigger background sync if cache is stale
            if (isCacheStale)
            {
                _ = Task.Run(async () => await SyncTransfersInBackgroundAsync(warehouseCode));
            }

            return response;
        }

        // No cached data - fetch first page from API and start background sync
        _logger.LogInformation("No cached transfers for warehouse {WarehouseCode}, fetching from API", warehouseCode);

        var apiResponse = await FetchTransfersFromApiAsync(warehouseCode, page, pageSize);
        if (apiResponse?.Transfers?.Any() == true)
        {
            // Save first page to cache (don't let cache save failure discard API results)
            try { await SaveTransfersToCacheAsync(apiResponse.Transfers); }
            catch (Exception saveEx) { _logger.LogWarning(saveEx, "Failed to cache transfers for warehouse {WarehouseCode}", warehouseCode); }

            // Start background sync for remaining items
            _ = Task.Run(async () => await SyncRemainingTransfersInBackgroundAsync(warehouseCode, apiResponse.HasMore));

            return apiResponse;
        }

        // API returned empty results - return a proper empty response
        return apiResponse ?? new InventoryTransferListResponse
        {
            Warehouse = warehouseCode,
            Page = page,
            PageSize = pageSize,
            Count = 0,
            HasMore = false,
            Transfers = new List<InventoryTransferDto>()
        };
    }

    public async Task<InventoryTransferDateResponse?> GetCachedTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Ensure dates are UTC for PostgreSQL compatibility
        var fromDateUtc = fromDate.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(fromDate, DateTimeKind.Utc) : fromDate.ToUniversalTime();
        var toDateUtc = toDate.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(toDate, DateTimeKind.Utc) : toDate.ToUniversalTime();

        var toDateEndUtc = toDateUtc.Date.AddDays(1);
        var transfers = await dbContext.CachedInventoryTransfers
            .Where(t => (t.FromWarehouse == warehouseCode || t.ToWarehouse == warehouseCode) &&
                       t.DocDate >= fromDateUtc && t.DocDate < toDateEndUtc)
            .OrderByDescending(t => t.DocDate)
            .ThenByDescending(t => t.DocNum)
            .ToListAsync();

        // If cache has data, return it (and trigger background sync if stale)
        if (transfers.Count > 0)
        {
            var cacheKey = $"InventoryTransfers_{warehouseCode}";
            var syncInfo = await dbContext.CacheSyncInfo.FindAsync(cacheKey);
            var isCacheStale = syncInfo == null ||
                              (DateTime.UtcNow - syncInfo.LastSyncedAt) > _cacheExpiration ||
                              !syncInfo.SyncSuccessful;

            if (isCacheStale)
            {
                _ = Task.Run(async () => await SyncTransfersInBackgroundAsync(warehouseCode));
            }

            return new InventoryTransferDateResponse
            {
                Warehouse = warehouseCode,
                FromDate = fromDate.ToString("yyyy-MM-dd"),
                ToDate = toDate.ToString("yyyy-MM-dd"),
                Count = transfers.Count,
                Transfers = transfers.Select(MapCachedToDto).ToList()
            };
        }

        // No cached data - fall back to API directly
        _logger.LogInformation("No cached transfers for warehouse {WarehouseCode} in date range, fetching from API", warehouseCode);
        try
        {
            var from = fromDate.ToString("yyyy-MM-dd");
            var to = toDate.ToString("yyyy-MM-dd");
            var apiResponse = await _httpClient.GetFromJsonAsync<InventoryTransferDateResponse>(
                $"api/inventorytransfer/{warehouseCode}/daterange?fromDate={from}&toDate={to}", _jsonOptions);

            if (apiResponse?.Transfers?.Any() == true)
            {
                // Cache the results (don't let cache save failure discard API results)
                try { await SaveTransfersToCacheAsync(apiResponse.Transfers); }
                catch (Exception saveEx) { _logger.LogWarning(saveEx, "Failed to cache transfers for warehouse {WarehouseCode}", warehouseCode); }
                // Trigger full background sync
                _ = Task.Run(async () => await SyncTransfersInBackgroundAsync(warehouseCode));
            }

            return apiResponse ?? new InventoryTransferDateResponse
            {
                Warehouse = warehouseCode,
                FromDate = from,
                ToDate = to,
                Count = 0,
                Transfers = new List<InventoryTransferDto>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching transfers by date range from API for warehouse {WarehouseCode}", warehouseCode);
            return new InventoryTransferDateResponse
            {
                Warehouse = warehouseCode,
                FromDate = fromDate.ToString("yyyy-MM-dd"),
                ToDate = toDate.ToString("yyyy-MM-dd"),
                Count = 0,
                Transfers = new List<InventoryTransferDto>()
            };
        }
    }

    public async Task<InventoryTransferDto?> GetCachedTransferByDocEntryAsync(int docEntry)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var cached = await dbContext.CachedInventoryTransfers
            .FirstOrDefaultAsync(t => t.DocEntry == docEntry);

        if (cached != null)
        {
            return MapCachedToDto(cached);
        }

        // Try to fetch from API
        try
        {
            var transfer = await _httpClient.GetFromJsonAsync<InventoryTransferDto>(
                $"api/inventorytransfer/doc/{docEntry}", _jsonOptions);

            if (transfer != null)
            {
                try { await SaveTransfersToCacheAsync(new List<InventoryTransferDto> { transfer }); }
                catch (Exception saveEx) { _logger.LogWarning(saveEx, "Failed to cache transfer {DocEntry}", docEntry); }
            }

            return transfer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching transfer {DocEntry} from API", docEntry);
            return null;
        }
    }

    public async Task<bool> SyncTransfersAsync(string warehouseCode)
    {
        return await SyncTransfersInBackgroundAsync(warehouseCode);
    }

    public async Task<CacheSyncInfo?> GetSyncStatusAsync(string warehouseCode)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var cacheKey = $"InventoryTransfers_{warehouseCode}";
        return await dbContext.CacheSyncInfo.FindAsync(cacheKey);
    }

    private async Task<bool> SyncTransfersInBackgroundAsync(string warehouseCode)
    {
        lock (_syncLock)
        {
            if (_syncInProgress.Contains(warehouseCode))
            {
                _logger.LogDebug("Transfer sync already in progress for warehouse {WarehouseCode}", warehouseCode);
                return false;
            }
            _syncInProgress.Add(warehouseCode);
        }

        try
        {
            _logger.LogInformation("Starting full transfer sync for warehouse {WarehouseCode}", warehouseCode);

            var allTransfers = new List<InventoryTransferDto>();
            var page = 1;
            var pageSize = 100;
            var hasMore = true;

            while (hasMore)
            {
                var response = await FetchTransfersFromApiAsync(warehouseCode, page, pageSize);
                if (response?.Transfers != null)
                {
                    allTransfers.AddRange(response.Transfers);
                    hasMore = response.HasMore;
                    page++;
                }
                else
                {
                    hasMore = false;
                }
            }

            if (allTransfers.Any())
            {
                await ReplaceTransferCacheAsync(warehouseCode, allTransfers);
                await UpdateSyncInfoAsync(warehouseCode, allTransfers.Count, true, null);
                _logger.LogInformation("Completed transfer sync for warehouse {WarehouseCode}: {Count} transfers", warehouseCode, allTransfers.Count);
                SyncCompleted?.Invoke(this, warehouseCode);
                return true;
            }
            else
            {
                await UpdateSyncInfoAsync(warehouseCode, 0, true, "No transfers found");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during transfer sync for warehouse {WarehouseCode}", warehouseCode);
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

    private async Task SyncRemainingTransfersInBackgroundAsync(string warehouseCode, bool hasMore)
    {
        if (!hasMore) return;

        lock (_syncLock)
        {
            if (_syncInProgress.Contains(warehouseCode))
            {
                _logger.LogDebug("Transfer sync already in progress for warehouse {WarehouseCode}", warehouseCode);
                return;
            }
            _syncInProgress.Add(warehouseCode);
        }

        try
        {
            _logger.LogInformation("Starting background sync for remaining transfers in warehouse {WarehouseCode}", warehouseCode);

            var page = 2;
            var pageSize = 100;

            while (hasMore)
            {
                var response = await FetchTransfersFromApiAsync(warehouseCode, page, pageSize);
                if (response?.Transfers != null && response.Transfers.Any())
                {
                    await SaveTransfersToCacheAsync(response.Transfers);
                    hasMore = response.HasMore;
                    page++;
                    _logger.LogDebug("Cached page {Page} for warehouse {WarehouseCode}: {Count} transfers",
                        page - 1, warehouseCode, response.Transfers.Count);
                }
                else
                {
                    hasMore = false;
                }
            }

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var totalCount = await dbContext.CachedInventoryTransfers
                .Where(t => t.FromWarehouse == warehouseCode || t.ToWarehouse == warehouseCode)
                .CountAsync();

            await UpdateSyncInfoAsync(warehouseCode, totalCount, true, null);
            _logger.LogInformation("Completed background transfer sync for warehouse {WarehouseCode}: {Count} total transfers", warehouseCode, totalCount);
            SyncCompleted?.Invoke(this, warehouseCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during background transfer sync for warehouse {WarehouseCode}", warehouseCode);
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

    private async Task<InventoryTransferListResponse?> FetchTransfersFromApiAsync(string warehouseCode, int page, int pageSize)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var response = await _httpClient.GetFromJsonAsync<InventoryTransferListResponse>(
                $"api/inventorytransfer/{warehouseCode}/paged?page={page}&pageSize={pageSize}", _jsonOptions, cts.Token);
            return response;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Timeout fetching transfers from API for warehouse {WarehouseCode}, page {Page}", warehouseCode, page);
            throw new TimeoutException($"Timed out fetching transfers for warehouse {warehouseCode}. SAP may be responding slowly.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching transfers from API for warehouse {WarehouseCode}, page {Page}", warehouseCode, page);
            throw; // Propagate so callers can show meaningful errors
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching transfers from API for warehouse {WarehouseCode}, page {Page}", warehouseCode, page);
            return null;
        }
    }

    private async Task SaveTransfersToCacheAsync(List<InventoryTransferDto> transfers)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        foreach (var transfer in transfers)
        {
            var existing = await dbContext.CachedInventoryTransfers
                .FirstOrDefaultAsync(t => t.DocEntry == transfer.DocEntry);

            var cached = existing ?? new CachedInventoryTransfer();
            MapDtoToCached(transfer, cached);
            cached.LastSyncedAt = now;

            if (existing == null)
            {
                dbContext.CachedInventoryTransfers.Add(cached);
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task ReplaceTransferCacheAsync(string warehouseCode, List<InventoryTransferDto> transfers)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Delete existing transfers for this warehouse (where it's from or to)
        await dbContext.CachedInventoryTransfers
            .Where(t => t.FromWarehouse == warehouseCode || t.ToWarehouse == warehouseCode)
            .ExecuteDeleteAsync();

        // Add all new items
        var now = DateTime.UtcNow;
        var cachedTransfers = transfers.Select(transfer =>
        {
            var cached = new CachedInventoryTransfer();
            MapDtoToCached(transfer, cached);
            cached.LastSyncedAt = now;
            return cached;
        }).ToList();

        dbContext.CachedInventoryTransfers.AddRange(cachedTransfers);
        await dbContext.SaveChangesAsync();
    }

    private async Task UpdateSyncInfoAsync(string warehouseCode, int count, bool success, string? error)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var cacheKey = $"InventoryTransfers_{warehouseCode}";
        var syncInfo = await dbContext.CacheSyncInfo.FindAsync(cacheKey);
        if (syncInfo == null)
        {
            syncInfo = new CacheSyncInfo { CacheKey = cacheKey };
            dbContext.CacheSyncInfo.Add(syncInfo);
        }

        syncInfo.LastSyncedAt = DateTime.UtcNow;
        syncInfo.ItemCount = count;
        syncInfo.SyncSuccessful = success;
        syncInfo.LastError = error;

        await dbContext.SaveChangesAsync();
    }

    private static void MapDtoToCached(InventoryTransferDto dto, CachedInventoryTransfer cached)
    {
        cached.DocEntry = dto.DocEntry;
        cached.DocNum = dto.DocNum;
        cached.DocDate = DateTime.TryParse(dto.DocDate, out var docDate) ? DateTime.SpecifyKind(docDate, DateTimeKind.Utc) : null;
        cached.DueDate = DateTime.TryParse(dto.DueDate, out var dueDate) ? DateTime.SpecifyKind(dueDate, DateTimeKind.Utc) : null;
        cached.FromWarehouse = dto.FromWarehouse;
        cached.ToWarehouse = dto.ToWarehouse;
        cached.Comments = dto.Comments;
        cached.LinesJson = dto.Lines != null ? JsonSerializer.Serialize(dto.Lines) : null;
    }

    private static InventoryTransferDto MapCachedToDto(CachedInventoryTransfer cached)
    {
        return new InventoryTransferDto
        {
            DocEntry = cached.DocEntry,
            DocNum = cached.DocNum,
            DocDate = cached.DocDate?.ToString("yyyy-MM-dd"),
            DueDate = cached.DueDate?.ToString("yyyy-MM-dd"),
            FromWarehouse = cached.FromWarehouse,
            ToWarehouse = cached.ToWarehouse,
            Comments = cached.Comments,
            Lines = !string.IsNullOrEmpty(cached.LinesJson)
                ? JsonSerializer.Deserialize<List<InventoryTransferLineDto>>(cached.LinesJson, _jsonOptions)
                : null
        };
    }
}
