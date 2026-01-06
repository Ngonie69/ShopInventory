using ShopInventory.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShopInventory.Web.Services;

public interface IProductService
{
    Task<WarehouseProductsResponse?> GetProductsInWarehouseAsync(string warehouseCode);
    Task<WarehouseProductsPagedResponse?> GetPagedProductsAsync(string warehouseCode, int page = 1, int pageSize = 20);
    Task<ProductBatchesResponse?> GetProductBatchesAsync(string itemCode, string warehouseCode);
    Task<ProductDto?> SearchProductByBarcodeAsync(string barcode, string warehouseCode);
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

    public async Task<WarehouseProductsResponse?> GetProductsInWarehouseAsync(string warehouseCode)
    {
        _logger.LogInformation("GetProductsInWarehouseAsync called for warehouse {WarehouseCode}", warehouseCode);
        try
        {
            // Use cached stock service - gets all items (paginated internally)
            var pagedResponse = await _stockCacheService.GetCachedStockAsync(warehouseCode, 1, 1000);
            if (pagedResponse == null)
            {
                _logger.LogWarning("GetCachedStockAsync returned null for warehouse {WarehouseCode}", warehouseCode);
                return null;
            }

            _logger.LogInformation("GetCachedStockAsync returned {Count} products for warehouse {WarehouseCode}",
                pagedResponse.Products?.Count ?? 0, warehouseCode);

            return new WarehouseProductsResponse
            {
                WarehouseCode = pagedResponse.WarehouseCode,
                TotalProducts = pagedResponse.Products?.Count ?? 0,
                ProductsWithBatches = 0,
                Products = pagedResponse.Products
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetProductsInWarehouseAsync for warehouse {WarehouseCode}: {Message}", warehouseCode, ex.Message);
            throw;
        }
    }

    public async Task<WarehouseProductsPagedResponse?> GetPagedProductsAsync(string warehouseCode, int page = 1, int pageSize = 20)
    {
        _logger.LogInformation("GetPagedProductsAsync called for warehouse {WarehouseCode}, page {Page}, pageSize {PageSize}", warehouseCode, page, pageSize);
        try
        {
            // Use cached stock service
            var result = await _stockCacheService.GetCachedStockAsync(warehouseCode, page, pageSize);
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

    public async Task<ProductBatchesResponse?> GetProductBatchesAsync(string itemCode, string warehouseCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ProductBatchesResponse>(
                $"api/product/{itemCode}/batches/{warehouseCode}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching batches for item {ItemCode} in warehouse {WarehouseCode}", itemCode, warehouseCode);
            return null;
        }
    }

    public async Task<ProductDto?> SearchProductByBarcodeAsync(string barcode, string warehouseCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ProductDto>(
                $"api/product/barcode/{barcode}/warehouse/{warehouseCode}", _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching for barcode {Barcode} in warehouse {WarehouseCode}", barcode, warehouseCode);
            return null;
        }
    }
}
