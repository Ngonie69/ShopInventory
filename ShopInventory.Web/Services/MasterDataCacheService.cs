using Blazored.LocalStorage;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// Service for caching master data locally to improve performance
/// All data is stored in PostgreSQL database for persistence
/// </summary>
public interface IMasterDataCacheService
{
    Task<List<BusinessPartnerDto>> GetBusinessPartnersAsync(bool forceRefresh = false);
    Task<List<ProductDto>> GetProductsAsync(bool forceRefresh = false);
    Task<List<ProductDto>> GetProductsAsync(string warehouseCode, bool forceRefresh = false);
    Task<List<WarehouseDto>> GetWarehousesAsync(bool forceRefresh = false);
    Task<List<GLAccountDto>> GetGLAccountsAsync(bool forceRefresh = false);
    Task<List<ItemPriceDto>> GetItemPricesAsync(bool forceRefresh = false);
    void InvalidateCache(string? cacheKey = null);
    DateTime? GetLastRefreshTime(string cacheKey);
    bool IsCacheReady { get; }
    Task PreloadCacheAsync();
    Task<int> SyncProductsFromApiAsync();
    Task<int> SyncPricesFromApiAsync();
    Task<int> SyncBusinessPartnersFromApiAsync();
    Task<int> SyncWarehousesFromApiAsync();
    Task<int> SyncGLAccountsFromApiAsync();
}

public class MasterDataCacheService : IMasterDataCacheService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MasterDataCacheService> _logger;
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly ILocalStorageService _localStorage;

    private const string BusinessPartnersCacheKey = "BusinessPartners";
    private const string ProductsCacheKey = "Products_All";
    private const string ProductsCacheKeyPrefix = "Products_";
    private const string WarehousesCacheKey = "Warehouses";
    private const string GLAccountsCacheKey = "GLAccounts";
    private const string ItemPricesCacheKey = "ItemPrices";

    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(1);

    // Static in-memory cache that persists across all Blazor circuits
    private static readonly ConcurrentDictionary<string, object> _staticCache = new();
    private static readonly ConcurrentDictionary<string, DateTime> _lastRefreshTimes = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _loadLocks = new();
    private static bool _isPreloaded = false;

    // In-memory cache for database data (fast reads)
    private static List<ProductDto>? _cachedProducts;
    private static List<BusinessPartnerDto>? _cachedBusinessPartners;
    private static List<WarehouseDto>? _cachedWarehouses;
    private static List<GLAccountDto>? _cachedGLAccounts;
    private static List<ItemPriceDto>? _cachedPrices;
    private static DateTime _productsLoadedAt = DateTime.MinValue;
    private static DateTime _bpLoadedAt = DateTime.MinValue;
    private static DateTime _warehousesLoadedAt = DateTime.MinValue;
    private static DateTime _glAccountsLoadedAt = DateTime.MinValue;
    private static DateTime _pricesLoadedAt = DateTime.MinValue;
    private static readonly TimeSpan MemoryCacheDuration = TimeSpan.FromMinutes(30);

    public bool IsCacheReady => _isPreloaded;

    public MasterDataCacheService(
        HttpClient httpClient,
        ILogger<MasterDataCacheService> logger,
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        ILocalStorageService localStorage)
    {
        _httpClient = httpClient;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _localStorage = localStorage;
    }

    /// <summary>
    /// Ensures the HttpClient has authentication header set from localStorage
    /// </summary>
    private async Task EnsureAuthenticationAsync()
    {
        try
        {
            // Only set if not already present
            if (_httpClient.DefaultRequestHeaders.Authorization == null)
            {
                var token = await _localStorage.GetItemAsync<string>("authToken");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    _logger.LogDebug("Set auth header from localStorage for API call");
                }
                else
                {
                    _logger.LogWarning("No auth token found in localStorage - API calls may fail");
                }
            }
        }
        catch (Exception ex)
        {
            // localStorage not available during prerendering
            _logger.LogDebug("Could not access localStorage for auth token: {Message}", ex.Message);
        }
    }

    private static SemaphoreSlim GetLoadLock(string key)
    {
        return _loadLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private bool IsCacheValid(string key)
    {
        if (!_staticCache.ContainsKey(key)) return false;
        if (!_lastRefreshTimes.TryGetValue(key, out var lastRefresh)) return false;
        return DateTime.Now - lastRefresh < TimeSpan.FromMinutes(30);
    }

    public async Task PreloadCacheAsync()
    {
        if (_isPreloaded) return;

        _logger.LogInformation("Preloading master data cache...");

        try
        {
            var warehousesTask = GetWarehousesAsync(false);
            var partnersTask = GetBusinessPartnersAsync(false);
            var productsTask = GetProductsAsync(false);
            var pricesTask = GetItemPricesAsync(false);

            await Task.WhenAll(warehousesTask, partnersTask, productsTask, pricesTask);
            _isPreloaded = true;
            _logger.LogInformation("Master data cache preloaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error preloading cache");
        }
    }

    #region Products

    /// <summary>
    /// Sync products from API to local database
    /// </summary>
    public async Task<int> SyncProductsFromApiAsync()
    {
        var loadLock = GetLoadLock(ProductsCacheKey);
        await loadLock.WaitAsync();

        try
        {
            _logger.LogInformation("Syncing products from API to database...");

            // Ensure auth header is set before API call
            await EnsureAuthenticationAsync();

            // Fetch products from API
            var response = await _httpClient.GetFromJsonAsync<ProductsResponse>("api/product");
            var apiProducts = response?.Products ?? new List<ProductDto>();

            if (apiProducts.Count == 0)
            {
                _logger.LogWarning("No products received from API");
                return 0;
            }

            // Fetch prices to include in products (from cached endpoint - synced from SAP every 5 mins)
            Dictionary<string, decimal> priceDict = new();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var pricesResponse = await _httpClient.GetFromJsonAsync<ItemPricesResponse>("api/price/cached", cts.Token);
                if (pricesResponse?.Prices != null)
                {
                    priceDict = pricesResponse.Prices
                        .Where(p => !string.IsNullOrEmpty(p.ItemCode))
                        .GroupBy(p => p.ItemCode!)
                        .ToDictionary(g => g.Key, g => g.First().Price);
                    _logger.LogInformation("Retrieved {Count} prices from API", priceDict.Count);
                }
            }
            catch (Exception priceEx)
            {
                _logger.LogWarning(priceEx, "Failed to fetch prices, continuing without prices");
            }

            // Save to database
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var syncTime = DateTime.UtcNow;
            var updatedCount = 0;
            var insertedCount = 0;

            // Get existing products in database
            var existingItemCodes = await db.CachedProducts
                .Select(p => p.ItemCode)
                .ToHashSetAsync();

            foreach (var apiProduct in apiProducts)
            {
                if (string.IsNullOrEmpty(apiProduct.ItemCode)) continue;

                var price = priceDict.TryGetValue(apiProduct.ItemCode, out var p) ? p : 0;

                if (existingItemCodes.Contains(apiProduct.ItemCode))
                {
                    // Update existing
                    await db.CachedProducts
                        .Where(cp => cp.ItemCode == apiProduct.ItemCode)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(cp => cp.ItemName, apiProduct.ItemName)
                            .SetProperty(cp => cp.BarCode, apiProduct.BarCode)
                            .SetProperty(cp => cp.ItemType, apiProduct.ItemType)
                            .SetProperty(cp => cp.ManagesBatches, apiProduct.ManagesBatches)
                            .SetProperty(cp => cp.Price, price)
                            .SetProperty(cp => cp.DefaultWarehouse, apiProduct.DefaultWarehouse)
                            .SetProperty(cp => cp.UoM, apiProduct.UoM)
                            .SetProperty(cp => cp.LastSyncedAt, syncTime)
                            .SetProperty(cp => cp.IsActive, true));
                    updatedCount++;
                }
                else
                {
                    // Insert new
                    db.CachedProducts.Add(new CachedProduct
                    {
                        ItemCode = apiProduct.ItemCode,
                        ItemName = apiProduct.ItemName,
                        BarCode = apiProduct.BarCode,
                        ItemType = apiProduct.ItemType,
                        ManagesBatches = apiProduct.ManagesBatches,
                        Price = price,
                        DefaultWarehouse = apiProduct.DefaultWarehouse,
                        UoM = apiProduct.UoM,
                        LastSyncedAt = syncTime,
                        IsActive = true
                    });
                    insertedCount++;
                }
            }

            // Mark products not in API as inactive
            var apiItemCodes = apiProducts
                .Where(p => !string.IsNullOrEmpty(p.ItemCode))
                .Select(p => p.ItemCode!)
                .ToHashSet();

            var deactivatedCount = await db.CachedProducts
                .Where(cp => cp.IsActive && !apiItemCodes.Contains(cp.ItemCode))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(cp => cp.IsActive, false)
                    .SetProperty(cp => cp.LastSyncedAt, syncTime));

            await db.SaveChangesAsync();

            // Update sync info
            await UpdateSyncInfoAsync(db, ProductsCacheKey, apiProducts.Count, true, null);
            _lastRefreshTimes[ProductsCacheKey] = DateTime.Now;

            _logger.LogInformation(
                "Products sync completed: {Inserted} inserted, {Updated} updated, {Deactivated} deactivated",
                insertedCount, updatedCount, deactivatedCount);

            return apiProducts.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync products from API");
            await UpdateSyncInfoAsync(null, ProductsCacheKey, 0, false, ex.Message);
            throw;
        }
        finally
        {
            loadLock.Release();
        }
    }

    public async Task<List<ProductDto>> GetProductsAsync(bool forceRefresh = false)
    {
        // Return from memory cache if valid
        if (!forceRefresh && _cachedProducts != null &&
            (DateTime.Now - _productsLoadedAt) < MemoryCacheDuration)
        {
            _logger.LogDebug("Returning {Count} products from memory cache", _cachedProducts.Count);
            return _cachedProducts;
        }

        var loadLock = GetLoadLock(ProductsCacheKey);
        await loadLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!forceRefresh && _cachedProducts != null &&
                (DateTime.Now - _productsLoadedAt) < MemoryCacheDuration)
            {
                return _cachedProducts;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            // ALWAYS load from database first for quick response
            var products = await db.CachedProducts
                .Where(p => p.IsActive)
                .OrderBy(p => p.ItemCode)
                .Select(p => new ProductDto
                {
                    ItemCode = p.ItemCode,
                    ItemName = p.ItemName,
                    BarCode = p.BarCode,
                    ItemType = p.ItemType,
                    ManagesBatches = p.ManagesBatches,
                    Price = p.Price,
                    DefaultWarehouse = p.DefaultWarehouse,
                    UoM = p.UoM
                })
                .ToListAsync();

            // Store in memory cache immediately
            _cachedProducts = products;
            _productsLoadedAt = DateTime.Now;
            _lastRefreshTimes[ProductsCacheKey] = DateTime.Now;

            _logger.LogDebug("Loaded {Count} products from database into memory cache", products.Count);

            // Check if we need to sync from API (in background if we have data)
            var needsSync = await NeedsSyncAsync(db, ProductsCacheKey, forceRefresh);

            if (needsSync)
            {
                if (products.Count > 0 && !forceRefresh)
                {
                    // We have data - sync in background without blocking
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncProductsFromApiAsync();
                            // Reload from database after sync
                            await using var bgDb = await _dbContextFactory.CreateDbContextAsync();
                            var updatedProducts = await bgDb.CachedProducts
                                .Where(p => p.IsActive)
                                .OrderBy(p => p.ItemCode)
                                .Select(p => new ProductDto
                                {
                                    ItemCode = p.ItemCode,
                                    ItemName = p.ItemName,
                                    BarCode = p.BarCode,
                                    ItemType = p.ItemType,
                                    ManagesBatches = p.ManagesBatches,
                                    Price = p.Price,
                                    DefaultWarehouse = p.DefaultWarehouse,
                                    UoM = p.UoM
                                })
                                .ToListAsync();
                            _cachedProducts = updatedProducts;
                            _productsLoadedAt = DateTime.Now;
                            _logger.LogInformation("Background sync completed, updated {Count} products in cache", updatedProducts.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Background sync of products failed");
                        }
                    });
                }
                else
                {
                    // No data in database - trigger background sync (non-blocking)
                    // This prevents page from hanging on slow API calls
                    _logger.LogInformation("No products in database, triggering background sync...");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncProductsFromApiAsync();
                            // Reload from database after sync
                            await using var bgDb = await _dbContextFactory.CreateDbContextAsync();
                            var updatedProducts = await bgDb.CachedProducts
                                .Where(p => p.IsActive)
                                .OrderBy(p => p.ItemCode)
                                .Select(p => new ProductDto
                                {
                                    ItemCode = p.ItemCode,
                                    ItemName = p.ItemName,
                                    BarCode = p.BarCode,
                                    ItemType = p.ItemType,
                                    ManagesBatches = p.ManagesBatches,
                                    Price = p.Price,
                                    DefaultWarehouse = p.DefaultWarehouse,
                                    UoM = p.UoM
                                })
                                .ToListAsync();
                            _cachedProducts = updatedProducts;
                            _productsLoadedAt = DateTime.Now;
                            _logger.LogInformation("Initial sync completed, loaded {Count} products", updatedProducts.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Initial background sync of products failed");
                        }
                    });
                }
            }

            return products;
        }
        finally
        {
            loadLock.Release();
        }
    }

    public async Task<List<ProductDto>> GetProductsAsync(string warehouseCode, bool forceRefresh = false)
    {
        var cacheKey = $"{ProductsCacheKeyPrefix}{warehouseCode}";

        if (!forceRefresh && IsCacheValid(cacheKey) &&
            _staticCache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogDebug("Returning {Count} cached products for warehouse {Warehouse}", ((List<ProductDto>)cached).Count, warehouseCode);
            return (List<ProductDto>)cached;
        }

        var loadLock = GetLoadLock(cacheKey);
        await loadLock.WaitAsync();
        try
        {
            if (!forceRefresh && IsCacheValid(cacheKey) &&
                _staticCache.TryGetValue(cacheKey, out cached))
            {
                return (List<ProductDto>)cached;
            }

            _logger.LogInformation("Fetching products for warehouse {Warehouse} from API...", warehouseCode);
            var response = await _httpClient.GetFromJsonAsync<WarehouseProductsResponse>($"api/product/warehouse/{warehouseCode}");
            var products = response?.Products ?? new List<ProductDto>();

            _staticCache[cacheKey] = products;
            _lastRefreshTimes[cacheKey] = DateTime.Now;

            _logger.LogInformation("Cached {Count} products for warehouse {Warehouse}", products.Count, warehouseCode);
            return products;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch products for warehouse {Warehouse}", warehouseCode);
            if (_staticCache.TryGetValue(cacheKey, out cached))
            {
                return (List<ProductDto>)cached;
            }
            return new List<ProductDto>();
        }
        finally
        {
            loadLock.Release();
        }
    }

    #endregion

    #region Prices

    /// <summary>
    /// Sync prices from API to local database
    /// </summary>
    public async Task<int> SyncPricesFromApiAsync()
    {
        var loadLock = GetLoadLock(ItemPricesCacheKey);
        await loadLock.WaitAsync();

        try
        {
            return await SyncPricesFromApiInternalAsync();
        }
        finally
        {
            loadLock.Release();
        }
    }

    /// <summary>
    /// Internal sync method that doesn't acquire the lock (caller must hold the lock)
    /// </summary>
    private async Task<int> SyncPricesFromApiInternalAsync()
    {
        _logger.LogInformation("Syncing prices from API cache to local database...");

        // Ensure auth header is set before API call
        await EnsureAuthenticationAsync();
        _logger.LogDebug("HttpClient Auth Header: {Auth}", _httpClient.DefaultRequestHeaders.Authorization?.ToString() ?? "NOT SET");

        // Use cached endpoint - prices are synced from SAP every 5 minutes by the API
        var httpResponse = await _httpClient.GetAsync("api/price/cached");
        _logger.LogDebug("API response status: {Status}", httpResponse.StatusCode);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorContent = await httpResponse.Content.ReadAsStringAsync();
            _logger.LogError("API call failed with status {Status}: {Error}", httpResponse.StatusCode, errorContent);
            throw new HttpRequestException($"API returned {httpResponse.StatusCode}: {errorContent}");
        }

        var response = await httpResponse.Content.ReadFromJsonAsync<ItemPricesResponse>();
        var apiPrices = response?.Prices ?? new List<ItemPriceDto>();

        if (apiPrices.Count == 0)
        {
            _logger.LogWarning("No prices received from API (response was valid but empty)");
            return 0;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var syncTime = DateTime.UtcNow;

        // Clear existing prices and insert new ones (simpler than upsert for prices)
        await db.CachedPrices.ExecuteDeleteAsync();

        foreach (var apiPrice in apiPrices)
        {
            if (string.IsNullOrEmpty(apiPrice.ItemCode)) continue;

            db.CachedPrices.Add(new CachedPrice
            {
                ItemCode = apiPrice.ItemCode,
                ItemName = apiPrice.ItemName,
                Price = apiPrice.Price,
                Currency = apiPrice.Currency,
                LastSyncedAt = syncTime
            });
        }

        await db.SaveChangesAsync();
        await UpdateSyncInfoAsync(db, ItemPricesCacheKey, apiPrices.Count, true, null);
        _lastRefreshTimes[ItemPricesCacheKey] = DateTime.Now;

        _logger.LogInformation("Prices sync completed: {Count} prices saved", apiPrices.Count);
        return apiPrices.Count;
    }

    public async Task<List<ItemPriceDto>> GetItemPricesAsync(bool forceRefresh = false)
    {
        // Return from memory cache if valid AND has data
        // Don't return empty cache - need to try to load data
        if (!forceRefresh && _cachedPrices != null && _cachedPrices.Count > 0 &&
            (DateTime.Now - _pricesLoadedAt) < MemoryCacheDuration)
        {
            _logger.LogDebug("Returning {Count} prices from memory cache", _cachedPrices.Count);
            return _cachedPrices;
        }

        var loadLock = GetLoadLock(ItemPricesCacheKey);
        await loadLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock - also verify count > 0
            if (!forceRefresh && _cachedPrices != null && _cachedPrices.Count > 0 &&
                (DateTime.Now - _pricesLoadedAt) < MemoryCacheDuration)
            {
                return _cachedPrices;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            // ALWAYS load from database first for quick response
            var prices = await db.CachedPrices
                .OrderBy(p => p.ItemCode)
                .Select(p => new ItemPriceDto
                {
                    ItemCode = p.ItemCode,
                    ItemName = p.ItemName,
                    Price = p.Price,
                    Currency = p.Currency
                })
                .ToListAsync();

            _logger.LogDebug("Loaded {Count} prices from database", prices.Count);

            // Check if we need to sync from API (in background if we have data)
            var needsSync = await NeedsSyncAsync(db, ItemPricesCacheKey, forceRefresh);
            _logger.LogDebug("Prices: needsSync={NeedsSync}, priceCount={Count}, forceRefresh={Force}", needsSync, prices.Count, forceRefresh);

            if (needsSync)
            {
                if (prices.Count > 0 && !forceRefresh)
                {
                    // Store in memory cache - we have data
                    _cachedPrices = prices;
                    _pricesLoadedAt = DateTime.Now;
                    _lastRefreshTimes[ItemPricesCacheKey] = DateTime.Now;

                    _logger.LogDebug("Prices: Starting background sync (have existing data)");
                    // We have data - sync in background without blocking
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncPricesFromApiInternalAsync();
                            // Reload from database after sync
                            await using var bgDb = await _dbContextFactory.CreateDbContextAsync();
                            var updatedPrices = await bgDb.CachedPrices
                                .OrderBy(p => p.ItemCode)
                                .Select(p => new ItemPriceDto
                                {
                                    ItemCode = p.ItemCode,
                                    ItemName = p.ItemName,
                                    Price = p.Price,
                                    Currency = p.Currency
                                })
                                .ToListAsync();
                            if (updatedPrices.Count > 0)
                            {
                                _cachedPrices = updatedPrices;
                                _pricesLoadedAt = DateTime.Now;
                                _logger.LogInformation("Background sync completed, updated {Count} prices in cache", updatedPrices.Count);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Background sync of prices failed");
                        }
                    });
                }
                else
                {
                    _logger.LogInformation("Prices: Starting blocking sync (database is empty or force refresh)");
                    // No data in database - must sync now (blocking)
                    try
                    {
                        await SyncPricesFromApiInternalAsync();
                        // Reload from database
                        prices = await db.CachedPrices
                            .OrderBy(p => p.ItemCode)
                            .Select(p => new ItemPriceDto
                            {
                                ItemCode = p.ItemCode,
                                ItemName = p.ItemName,
                                Price = p.Price,
                                Currency = p.Currency
                            })
                            .ToListAsync();

                        // Only cache if we got data
                        if (prices.Count > 0)
                        {
                            _cachedPrices = prices;
                            _pricesLoadedAt = DateTime.Now;
                            _lastRefreshTimes[ItemPricesCacheKey] = DateTime.Now;
                        }
                        _logger.LogInformation("Blocking sync completed, loaded {Count} prices", prices.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to sync prices from API - database is empty");
                    }
                }
            }
            else if (prices.Count > 0)
            {
                // No sync needed but we have data - cache it
                _cachedPrices = prices;
                _pricesLoadedAt = DateTime.Now;
                _lastRefreshTimes[ItemPricesCacheKey] = DateTime.Now;
            }

            return prices;
        }
        finally
        {
            loadLock.Release();
        }
    }

    #endregion

    #region Business Partners

    /// <summary>
    /// Sync business partners from API to local database
    /// </summary>
    public async Task<int> SyncBusinessPartnersFromApiAsync()
    {
        var loadLock = GetLoadLock(BusinessPartnersCacheKey);
        await loadLock.WaitAsync();

        try
        {
            _logger.LogInformation("Syncing business partners from API to database...");

            // Ensure auth header is set before API call
            await EnsureAuthenticationAsync();

            var response = await _httpClient.GetFromJsonAsync<BusinessPartnerListResponse>("api/businesspartner");
            var apiPartners = response?.BusinessPartners ?? new List<BusinessPartnerDto>();

            if (apiPartners.Count == 0)
            {
                _logger.LogWarning("No business partners received from API");
                return 0;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var syncTime = DateTime.UtcNow;
            var updatedCount = 0;
            var insertedCount = 0;

            var existingCardCodes = await db.CachedBusinessPartners
                .Select(p => p.CardCode)
                .ToHashSetAsync();

            foreach (var partner in apiPartners)
            {
                if (string.IsNullOrEmpty(partner.CardCode)) continue;

                if (existingCardCodes.Contains(partner.CardCode))
                {
                    await db.CachedBusinessPartners
                        .Where(cp => cp.CardCode == partner.CardCode)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(cp => cp.CardName, partner.CardName)
                            .SetProperty(cp => cp.CardType, partner.CardType)
                            .SetProperty(cp => cp.GroupCode, partner.GroupCode)
                            .SetProperty(cp => cp.Phone1, partner.Phone1)
                            .SetProperty(cp => cp.Phone2, partner.Phone2)
                            .SetProperty(cp => cp.Email, partner.Email)
                            .SetProperty(cp => cp.Address, partner.Address)
                            .SetProperty(cp => cp.City, partner.City)
                            .SetProperty(cp => cp.Country, partner.Country)
                            .SetProperty(cp => cp.Currency, partner.Currency)
                            .SetProperty(cp => cp.Balance, partner.Balance)
                            .SetProperty(cp => cp.IsActive, partner.IsActive)
                            .SetProperty(cp => cp.LastSyncedAt, syncTime));
                    updatedCount++;
                }
                else
                {
                    db.CachedBusinessPartners.Add(new CachedBusinessPartner
                    {
                        CardCode = partner.CardCode,
                        CardName = partner.CardName,
                        CardType = partner.CardType,
                        GroupCode = partner.GroupCode,
                        Phone1 = partner.Phone1,
                        Phone2 = partner.Phone2,
                        Email = partner.Email,
                        Address = partner.Address,
                        City = partner.City,
                        Country = partner.Country,
                        Currency = partner.Currency,
                        Balance = partner.Balance,
                        IsActive = partner.IsActive,
                        LastSyncedAt = syncTime
                    });
                    insertedCount++;
                }
            }

            await db.SaveChangesAsync();
            await UpdateSyncInfoAsync(db, BusinessPartnersCacheKey, apiPartners.Count, true, null);
            _lastRefreshTimes[BusinessPartnersCacheKey] = DateTime.Now;

            _logger.LogInformation(
                "Business partners sync completed: {Inserted} inserted, {Updated} updated",
                insertedCount, updatedCount);

            return apiPartners.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync business partners from API");
            await UpdateSyncInfoAsync(null, BusinessPartnersCacheKey, 0, false, ex.Message);
            throw;
        }
        finally
        {
            loadLock.Release();
        }
    }

    public async Task<List<BusinessPartnerDto>> GetBusinessPartnersAsync(bool forceRefresh = false)
    {
        // Return from memory cache if valid
        if (!forceRefresh && _cachedBusinessPartners != null &&
            (DateTime.Now - _bpLoadedAt) < MemoryCacheDuration)
        {
            _logger.LogDebug("Returning {Count} business partners from memory cache", _cachedBusinessPartners.Count);
            return _cachedBusinessPartners;
        }

        var loadLock = GetLoadLock(BusinessPartnersCacheKey);
        await loadLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!forceRefresh && _cachedBusinessPartners != null &&
                (DateTime.Now - _bpLoadedAt) < MemoryCacheDuration)
            {
                return _cachedBusinessPartners;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            // ALWAYS load from database first for quick response
            var partners = await db.CachedBusinessPartners
                .Where(p => p.IsActive)
                .OrderBy(p => p.CardCode)
                .Select(p => new BusinessPartnerDto
                {
                    CardCode = p.CardCode,
                    CardName = p.CardName,
                    CardType = p.CardType,
                    GroupCode = p.GroupCode,
                    Phone1 = p.Phone1,
                    Phone2 = p.Phone2,
                    Email = p.Email,
                    Address = p.Address,
                    City = p.City,
                    Country = p.Country,
                    Currency = p.Currency,
                    Balance = p.Balance,
                    IsActive = p.IsActive
                })
                .ToListAsync();

            // Store in memory cache immediately
            _cachedBusinessPartners = partners;
            _bpLoadedAt = DateTime.Now;
            _lastRefreshTimes[BusinessPartnersCacheKey] = DateTime.Now;

            _logger.LogDebug("Loaded {Count} business partners from database into memory cache", partners.Count);

            // Check if we need to sync from API (in background if we have data)
            var needsSync = await NeedsSyncAsync(db, BusinessPartnersCacheKey, forceRefresh);

            if (needsSync)
            {
                if (partners.Count > 0 && !forceRefresh)
                {
                    // We have data - sync in background without blocking
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncBusinessPartnersFromApiAsync();
                            // Reload from database after sync
                            await using var bgDb = await _dbContextFactory.CreateDbContextAsync();
                            var updatedPartners = await bgDb.CachedBusinessPartners
                                .Where(p => p.IsActive)
                                .OrderBy(p => p.CardCode)
                                .Select(p => new BusinessPartnerDto
                                {
                                    CardCode = p.CardCode,
                                    CardName = p.CardName,
                                    CardType = p.CardType,
                                    GroupCode = p.GroupCode,
                                    Phone1 = p.Phone1,
                                    Phone2 = p.Phone2,
                                    Email = p.Email,
                                    Address = p.Address,
                                    City = p.City,
                                    Country = p.Country,
                                    Currency = p.Currency,
                                    Balance = p.Balance,
                                    IsActive = p.IsActive
                                })
                                .ToListAsync();
                            _cachedBusinessPartners = updatedPartners;
                            _bpLoadedAt = DateTime.Now;
                            _logger.LogInformation("Background sync completed, updated {Count} business partners in cache", updatedPartners.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Background sync of business partners failed");
                        }
                    });
                }
                else
                {
                    // No data in database - trigger background sync (non-blocking)
                    // This prevents page from hanging on slow API calls
                    _logger.LogInformation("No business partners in database, triggering background sync...");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncBusinessPartnersFromApiAsync();
                            // Reload from database after sync
                            await using var bgDb = await _dbContextFactory.CreateDbContextAsync();
                            var updatedPartners = await bgDb.CachedBusinessPartners
                                .Where(p => p.IsActive)
                                .OrderBy(p => p.CardCode)
                                .Select(p => new BusinessPartnerDto
                                {
                                    CardCode = p.CardCode,
                                    CardName = p.CardName,
                                    CardType = p.CardType,
                                    GroupCode = p.GroupCode,
                                    Phone1 = p.Phone1,
                                    Phone2 = p.Phone2,
                                    Email = p.Email,
                                    Address = p.Address,
                                    City = p.City,
                                    Country = p.Country,
                                    Currency = p.Currency,
                                    Balance = p.Balance,
                                    IsActive = p.IsActive
                                })
                                .ToListAsync();
                            _cachedBusinessPartners = updatedPartners;
                            _bpLoadedAt = DateTime.Now;
                            _logger.LogInformation("Initial sync completed, loaded {Count} business partners", updatedPartners.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Initial background sync of business partners failed");
                        }
                    });
                }
            }

            return partners;
        }
        finally
        {
            loadLock.Release();
        }
    }

    #endregion

    #region Warehouses

    /// <summary>
    /// Sync warehouses from API to local database
    /// </summary>
    public async Task<int> SyncWarehousesFromApiAsync()
    {
        var loadLock = GetLoadLock(WarehousesCacheKey);
        await loadLock.WaitAsync();

        try
        {
            _logger.LogInformation("Syncing warehouses from API to database...");

            // Ensure auth header is set before API call
            await EnsureAuthenticationAsync();

            var response = await _httpClient.GetFromJsonAsync<WarehouseListResponse>("api/stock/warehouses");
            var apiWarehouses = response?.Warehouses ?? new List<WarehouseDto>();

            if (apiWarehouses.Count == 0)
            {
                _logger.LogWarning("No warehouses received from API");
                return 0;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var syncTime = DateTime.UtcNow;
            var updatedCount = 0;
            var insertedCount = 0;

            var existingCodes = await db.CachedWarehouses
                .Select(p => p.WarehouseCode)
                .ToHashSetAsync();

            foreach (var warehouse in apiWarehouses)
            {
                if (string.IsNullOrEmpty(warehouse.WarehouseCode)) continue;

                if (existingCodes.Contains(warehouse.WarehouseCode))
                {
                    await db.CachedWarehouses
                        .Where(cp => cp.WarehouseCode == warehouse.WarehouseCode)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(cp => cp.WarehouseName, warehouse.WarehouseName)
                            .SetProperty(cp => cp.Location, warehouse.Location)
                            .SetProperty(cp => cp.Street, warehouse.Street)
                            .SetProperty(cp => cp.City, warehouse.City)
                            .SetProperty(cp => cp.Country, warehouse.Country)
                            .SetProperty(cp => cp.IsActive, warehouse.IsActive)
                            .SetProperty(cp => cp.LastSyncedAt, syncTime));
                    updatedCount++;
                }
                else
                {
                    db.CachedWarehouses.Add(new CachedWarehouse
                    {
                        WarehouseCode = warehouse.WarehouseCode,
                        WarehouseName = warehouse.WarehouseName,
                        Location = warehouse.Location,
                        Street = warehouse.Street,
                        City = warehouse.City,
                        Country = warehouse.Country,
                        IsActive = warehouse.IsActive,
                        LastSyncedAt = syncTime
                    });
                    insertedCount++;
                }
            }

            await db.SaveChangesAsync();
            await UpdateSyncInfoAsync(db, WarehousesCacheKey, apiWarehouses.Count, true, null);
            _lastRefreshTimes[WarehousesCacheKey] = DateTime.Now;

            _logger.LogInformation(
                "Warehouses sync completed: {Inserted} inserted, {Updated} updated",
                insertedCount, updatedCount);

            return apiWarehouses.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync warehouses from API");
            await UpdateSyncInfoAsync(null, WarehousesCacheKey, 0, false, ex.Message);
            throw;
        }
        finally
        {
            loadLock.Release();
        }
    }

    public async Task<List<WarehouseDto>> GetWarehousesAsync(bool forceRefresh = false)
    {
        // Return from memory cache if valid
        if (!forceRefresh && _cachedWarehouses != null &&
            (DateTime.Now - _warehousesLoadedAt) < MemoryCacheDuration)
        {
            _logger.LogDebug("Returning {Count} warehouses from memory cache", _cachedWarehouses.Count);
            return _cachedWarehouses;
        }

        var loadLock = GetLoadLock(WarehousesCacheKey);
        await loadLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!forceRefresh && _cachedWarehouses != null &&
                (DateTime.Now - _warehousesLoadedAt) < MemoryCacheDuration)
            {
                return _cachedWarehouses;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            // ALWAYS load from database first for quick response
            var warehouses = await db.CachedWarehouses
                .Where(p => p.IsActive)
                .OrderBy(p => p.WarehouseCode)
                .Select(p => new WarehouseDto
                {
                    WarehouseCode = p.WarehouseCode,
                    WarehouseName = p.WarehouseName,
                    Location = p.Location,
                    Street = p.Street,
                    City = p.City,
                    Country = p.Country,
                    IsActive = p.IsActive
                })
                .ToListAsync();

            // Store in memory cache immediately
            _cachedWarehouses = warehouses;
            _warehousesLoadedAt = DateTime.Now;
            _lastRefreshTimes[WarehousesCacheKey] = DateTime.Now;

            _logger.LogDebug("Loaded {Count} warehouses from database into memory cache", warehouses.Count);

            // Check if we need to sync from API (in background if we have data)
            var needsSync = await NeedsSyncAsync(db, WarehousesCacheKey, forceRefresh);

            if (needsSync)
            {
                if (warehouses.Count > 0 && !forceRefresh)
                {
                    // We have data - sync in background without blocking
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncWarehousesFromApiAsync();
                            // Reload from database after sync
                            await using var bgDb = await _dbContextFactory.CreateDbContextAsync();
                            var updatedWarehouses = await bgDb.CachedWarehouses
                                .Where(p => p.IsActive)
                                .OrderBy(p => p.WarehouseCode)
                                .Select(p => new WarehouseDto
                                {
                                    WarehouseCode = p.WarehouseCode,
                                    WarehouseName = p.WarehouseName,
                                    Location = p.Location,
                                    Street = p.Street,
                                    City = p.City,
                                    Country = p.Country,
                                    IsActive = p.IsActive
                                })
                                .ToListAsync();
                            _cachedWarehouses = updatedWarehouses;
                            _warehousesLoadedAt = DateTime.Now;
                            _logger.LogInformation("Background sync completed, updated {Count} warehouses in cache", updatedWarehouses.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Background sync of warehouses failed");
                        }
                    });
                }
                else
                {
                    // No data in database - trigger sync in background (non-blocking)
                    // User will see empty list initially, but sync will populate data
                    _logger.LogInformation("No warehouses in database, triggering background sync...");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncWarehousesFromApiAsync();
                            // Reload from database after sync
                            await using var bgDb = await _dbContextFactory.CreateDbContextAsync();
                            var updatedWarehouses = await bgDb.CachedWarehouses
                                .Where(p => p.IsActive)
                                .OrderBy(p => p.WarehouseCode)
                                .Select(p => new WarehouseDto
                                {
                                    WarehouseCode = p.WarehouseCode,
                                    WarehouseName = p.WarehouseName,
                                    Location = p.Location,
                                    Street = p.Street,
                                    City = p.City,
                                    Country = p.Country,
                                    IsActive = p.IsActive
                                })
                                .ToListAsync();
                            _cachedWarehouses = updatedWarehouses;
                            _warehousesLoadedAt = DateTime.Now;
                            _logger.LogInformation("Initial sync completed, loaded {Count} warehouses", updatedWarehouses.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Initial background sync of warehouses failed");
                        }
                    });
                }
            }

            return warehouses;
        }
        finally
        {
            loadLock.Release();
        }
    }

    #endregion

    #region GL Accounts

    /// <summary>
    /// Sync G/L accounts from API to local database
    /// </summary>
    public async Task<int> SyncGLAccountsFromApiAsync()
    {
        var loadLock = GetLoadLock(GLAccountsCacheKey);
        await loadLock.WaitAsync();

        try
        {
            _logger.LogInformation("Syncing G/L accounts from API to database...");

            // Ensure auth header is set before API call
            await EnsureAuthenticationAsync();

            var response = await _httpClient.GetFromJsonAsync<GLAccountListResponse>("api/glaccount");
            var apiAccounts = response?.Accounts ?? new List<GLAccountDto>();

            if (apiAccounts.Count == 0)
            {
                _logger.LogWarning("No G/L accounts received from API");
                return 0;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var syncTime = DateTime.UtcNow;
            var updatedCount = 0;
            var insertedCount = 0;

            var existingCodes = await db.CachedGLAccounts
                .Select(p => p.Code)
                .ToHashSetAsync();

            foreach (var account in apiAccounts)
            {
                if (string.IsNullOrEmpty(account.Code)) continue;

                if (existingCodes.Contains(account.Code))
                {
                    await db.CachedGLAccounts
                        .Where(cp => cp.Code == account.Code)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(cp => cp.Name, account.Name)
                            .SetProperty(cp => cp.AccountType, account.AccountType)
                            .SetProperty(cp => cp.Currency, account.Currency)
                            .SetProperty(cp => cp.Balance, account.Balance)
                            .SetProperty(cp => cp.IsActive, account.IsActive)
                            .SetProperty(cp => cp.LastSyncedAt, syncTime));
                    updatedCount++;
                }
                else
                {
                    db.CachedGLAccounts.Add(new CachedGLAccount
                    {
                        Code = account.Code,
                        Name = account.Name,
                        AccountType = account.AccountType,
                        Currency = account.Currency,
                        Balance = account.Balance,
                        IsActive = account.IsActive,
                        LastSyncedAt = syncTime
                    });
                    insertedCount++;
                }
            }

            await db.SaveChangesAsync();
            await UpdateSyncInfoAsync(db, GLAccountsCacheKey, apiAccounts.Count, true, null);
            _lastRefreshTimes[GLAccountsCacheKey] = DateTime.Now;

            _logger.LogInformation(
                "G/L accounts sync completed: {Inserted} inserted, {Updated} updated",
                insertedCount, updatedCount);

            return apiAccounts.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync G/L accounts from API");
            await UpdateSyncInfoAsync(null, GLAccountsCacheKey, 0, false, ex.Message);
            throw;
        }
        finally
        {
            loadLock.Release();
        }
    }

    public async Task<List<GLAccountDto>> GetGLAccountsAsync(bool forceRefresh = false)
    {
        // Return from memory cache if valid
        if (!forceRefresh && _cachedGLAccounts != null &&
            (DateTime.Now - _glAccountsLoadedAt) < MemoryCacheDuration)
        {
            _logger.LogDebug("Returning {Count} G/L accounts from memory cache", _cachedGLAccounts.Count);
            return _cachedGLAccounts;
        }

        var loadLock = GetLoadLock(GLAccountsCacheKey);
        await loadLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!forceRefresh && _cachedGLAccounts != null &&
                (DateTime.Now - _glAccountsLoadedAt) < MemoryCacheDuration)
            {
                return _cachedGLAccounts;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            // ALWAYS load from database first for quick response
            var accounts = await db.CachedGLAccounts
                .Where(p => p.IsActive)
                .OrderBy(p => p.Code)
                .Select(p => new GLAccountDto
                {
                    Code = p.Code,
                    Name = p.Name,
                    AccountType = p.AccountType,
                    Currency = p.Currency,
                    Balance = p.Balance,
                    IsActive = p.IsActive
                })
                .ToListAsync();

            // Store in memory cache immediately
            _cachedGLAccounts = accounts;
            _glAccountsLoadedAt = DateTime.Now;
            _lastRefreshTimes[GLAccountsCacheKey] = DateTime.Now;

            _logger.LogDebug("Loaded {Count} G/L accounts from database into memory cache", accounts.Count);

            // Check if we need to sync from API (in background - always non-blocking)
            var needsSync = await NeedsSyncAsync(db, GLAccountsCacheKey, forceRefresh);

            if (needsSync)
            {
                // Always sync in background - never block the UI
                _logger.LogInformation("G/L accounts need sync, triggering background sync...");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SyncGLAccountsFromApiAsync();
                        // Reload from database after sync
                        await using var bgDb = await _dbContextFactory.CreateDbContextAsync();
                        var updatedAccounts = await bgDb.CachedGLAccounts
                            .Where(p => p.IsActive)
                            .OrderBy(p => p.Code)
                            .Select(p => new GLAccountDto
                            {
                                Code = p.Code,
                                Name = p.Name,
                                AccountType = p.AccountType,
                                Currency = p.Currency,
                                Balance = p.Balance,
                                IsActive = p.IsActive
                            })
                            .ToListAsync();
                        _cachedGLAccounts = updatedAccounts;
                        _glAccountsLoadedAt = DateTime.Now;
                        _logger.LogInformation("Background sync completed, loaded {Count} G/L accounts", updatedAccounts.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background sync of G/L accounts failed");
                    }
                });
            }

            return accounts;
        }
        finally
        {
            loadLock.Release();
        }
    }

    #endregion

    #region Helper Methods

    private async Task<bool> NeedsSyncAsync(WebAppDbContext db, string cacheKey, bool forceRefresh)
    {
        if (forceRefresh) return true;

        var syncInfo = await db.CacheSyncInfo.FindAsync(cacheKey);
        if (syncInfo == null) return true;

        var hasData = cacheKey switch
        {
            var k when k == ProductsCacheKey => await db.CachedProducts.AnyAsync(),
            var k when k == ItemPricesCacheKey => await db.CachedPrices.AnyAsync(),
            var k when k == BusinessPartnersCacheKey => await db.CachedBusinessPartners.AnyAsync(),
            var k when k == WarehousesCacheKey => await db.CachedWarehouses.AnyAsync(),
            var k when k == GLAccountsCacheKey => await db.CachedGLAccounts.AnyAsync(),
            _ => false
        };

        if (!hasData) return true;

        return (DateTime.UtcNow - syncInfo.LastSyncedAt) > SyncInterval;
    }

    private async Task UpdateSyncInfoAsync(WebAppDbContext? db, string cacheKey, int itemCount, bool success, string? error)
    {
        try
        {
            var shouldDispose = db == null;
            db ??= await _dbContextFactory.CreateDbContextAsync();

            try
            {
                var syncInfo = await db.CacheSyncInfo.FindAsync(cacheKey);
                if (syncInfo == null)
                {
                    syncInfo = new CacheSyncInfo { CacheKey = cacheKey };
                    db.CacheSyncInfo.Add(syncInfo);
                }
                syncInfo.LastSyncedAt = DateTime.UtcNow;
                syncInfo.ItemCount = itemCount;
                syncInfo.SyncSuccessful = success;
                syncInfo.LastError = error?.Length > 500 ? error[..500] : error;
                await db.SaveChangesAsync();
            }
            finally
            {
                if (shouldDispose)
                {
                    await db.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update sync info for {CacheKey}", cacheKey);
        }
    }

    public void InvalidateCache(string? cacheKey = null)
    {
        if (string.IsNullOrEmpty(cacheKey))
        {
            _staticCache.Clear();
            _lastRefreshTimes.Clear();
            _isPreloaded = false;

            // Clear all memory caches
            _cachedProducts = null;
            _cachedBusinessPartners = null;
            _cachedWarehouses = null;
            _cachedGLAccounts = null;
            _cachedPrices = null;

            _logger.LogInformation("All master data cache invalidated (including memory cache)");
        }
        else
        {
            _staticCache.TryRemove(cacheKey, out _);
            _lastRefreshTimes.TryRemove(cacheKey, out _);

            // Clear specific memory cache based on key
            switch (cacheKey)
            {
                case ProductsCacheKey:
                    _cachedProducts = null;
                    break;
                case BusinessPartnersCacheKey:
                    _cachedBusinessPartners = null;
                    break;
                case WarehousesCacheKey:
                    _cachedWarehouses = null;
                    break;
                case GLAccountsCacheKey:
                    _cachedGLAccounts = null;
                    break;
                case ItemPricesCacheKey:
                    _cachedPrices = null;
                    break;
            }

            _logger.LogInformation("Cache invalidated for key: {CacheKey}", cacheKey);
        }
    }

    public DateTime? GetLastRefreshTime(string cacheKey)
    {
        return _lastRefreshTimes.TryGetValue(cacheKey, out var time) ? time : null;
    }

    #endregion
}
