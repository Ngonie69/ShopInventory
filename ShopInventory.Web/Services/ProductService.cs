using ShopInventory.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

public interface IProductService
{
    Task<WarehouseProductsResponse?> GetProductsInWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);
    Task<WarehouseProductsPagedResponse?> GetPagedProductsAsync(
        string warehouseCode,
        int page = 1,
        int pageSize = 20,
        string? search = null,
        CancellationToken cancellationToken = default);
    Task<ProductBatchesResponse?> GetProductBatchesAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);
    Task<ProductDto?> SearchProductByBarcodeAsync(
        string barcode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Warehouse-wide stock counts, for figures shown beside a paged list.
    /// </summary>
    Task<WarehouseStockSummary> GetStockSummaryAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// When the warehouse's stock cache was last refreshed from SAP, or null if it
    /// has never synced.
    /// </summary>
    Task<DateTime?> GetLastSyncedAtAsync(string warehouseCode);

    /// <summary>
    /// Forces the warehouse's stock cache to resync from SAP now — the same walk the
    /// service already runs in the background when the cache goes stale.
    /// </summary>
    Task<bool> RefreshStockAsync(string warehouseCode);
}

public class ProductService : IProductService
{
    private readonly IWarehouseStockCacheService _stockCacheService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProductService> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ProductService(
        IWarehouseStockCacheService stockCacheService,
        HttpClient httpClient,
        ILogger<ProductService> logger)
    {
        _stockCacheService = stockCacheService;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<WarehouseProductsResponse?> GetProductsInWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GetProductsInWarehouseAsync called for warehouse {WarehouseCode}", warehouseCode);
        try
        {
            // Use cached stock service - gets ALL items (no pagination limit)
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _stockCacheService.GetAllCachedStockAsync(warehouseCode);
            if (response == null)
            {
                _logger.LogWarning("GetAllCachedStockAsync returned null for warehouse {WarehouseCode}", warehouseCode);
                return null;
            }

            _logger.LogInformation("GetAllCachedStockAsync returned {Count} products for warehouse {WarehouseCode}",
                response.Products?.Count ?? 0, warehouseCode);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetProductsInWarehouseAsync for warehouse {WarehouseCode}: {Message}", warehouseCode, ex.Message);
            throw;
        }
    }

    public async Task<WarehouseProductsPagedResponse?> GetPagedProductsAsync(
        string warehouseCode,
        int page = 1,
        int pageSize = 20,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GetPagedProductsAsync called for warehouse {WarehouseCode}, page {Page}, pageSize {PageSize}", warehouseCode, page, pageSize);
        try
        {
            // Use cached stock service
            var result = await _stockCacheService.GetCachedStockAsync(
                warehouseCode,
                page,
                pageSize,
                search,
                cancellationToken);
            _logger.LogInformation("GetCachedStockAsync returned {Count} products for warehouse {WarehouseCode}",
                result?.Products?.Count ?? 0, warehouseCode);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetPagedProductsAsync for warehouse {WarehouseCode}: {Message}", warehouseCode, ex.Message);
            throw;
        }
    }

    public async Task<ProductBatchesResponse?> GetProductBatchesAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Route as ProductController declares it. This read
            // "api/product/{itemCode}/batches/{warehouseCode}" until 2026-08-03,
            // which matches no route on the API and 404s for every item.
            var encodedWarehouse = Uri.EscapeDataString(warehouseCode);
            var encodedItem = Uri.EscapeDataString(itemCode);
            return await _httpClient.GetFromJsonAsync<ProductBatchesResponse>(
                $"api/product/warehouse/{encodedWarehouse}/item/{encodedItem}/batches",
                _jsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching batches for item {ItemCode} in warehouse {WarehouseCode}", itemCode, warehouseCode);
            return null;
        }
    }

    // Served from the warehouse stock cache, which already holds the barcode. The
    // API has no barcode endpoint — this called
    // "api/product/barcode/{barcode}/warehouse/{warehouseCode}" until 2026-08-03,
    // a route that has never existed, so every scan 404'd and the page reported the
    // barcode as not found.
    public async Task<ProductDto?> SearchProductByBarcodeAsync(
        string barcode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _stockCacheService.FindByBarcodeAsync(warehouseCode, barcode, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching for barcode {Barcode} in warehouse {WarehouseCode}", barcode, warehouseCode);
            return null;
        }
    }

    public Task<WarehouseStockSummary> GetStockSummaryAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
        => _stockCacheService.GetStockSummaryAsync(warehouseCode, cancellationToken);

    public async Task<DateTime?> GetLastSyncedAtAsync(string warehouseCode)
    {
        try
        {
            var syncInfo = await _stockCacheService.GetSyncStatusAsync(warehouseCode);
            return syncInfo?.LastSyncedAt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read stock sync status for warehouse {WarehouseCode}", warehouseCode);
            return null;
        }
    }

    public async Task<bool> RefreshStockAsync(string warehouseCode)
    {
        try
        {
            return await _stockCacheService.SyncWarehouseStockAsync(warehouseCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing stock for warehouse {WarehouseCode}", warehouseCode);
            return false;
        }
    }
}
