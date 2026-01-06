using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IInventoryTransferService
{
    Task<InventoryTransferListResponse?> GetTransfersByWarehouseAsync(string warehouseCode);
    Task<InventoryTransferListResponse?> GetPagedTransfersAsync(string warehouseCode, int page = 1, int pageSize = 20);
    Task<InventoryTransferDto?> GetTransferByDocEntryAsync(int docEntry);
    Task<InventoryTransferDateResponse?> GetTransfersByDateAsync(string warehouseCode, DateTime date);
    Task<InventoryTransferDateResponse?> GetTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate);
}

public class InventoryTransferService : IInventoryTransferService
{
    private readonly HttpClient _httpClient;
    private readonly IInventoryTransferCacheService _cacheService;
    private readonly ILogger<InventoryTransferService> _logger;

    public InventoryTransferService(
        HttpClient httpClient,
        IInventoryTransferCacheService cacheService,
        ILogger<InventoryTransferService> logger)
    {
        _httpClient = httpClient;
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task<InventoryTransferListResponse?> GetTransfersByWarehouseAsync(string warehouseCode)
    {
        try
        {
            // Use cache service for faster loading
            return await _cacheService.GetCachedTransfersAsync(warehouseCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfers from cache, falling back to API");
            try
            {
                return await _httpClient.GetFromJsonAsync<InventoryTransferListResponse>($"api/inventorytransfer/{warehouseCode}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<InventoryTransferListResponse?> GetPagedTransfersAsync(string warehouseCode, int page = 1, int pageSize = 20)
    {
        try
        {
            // Use cache service for faster loading
            return await _cacheService.GetCachedTransfersAsync(warehouseCode, page, pageSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting paged transfers from cache, falling back to API");
            try
            {
                return await _httpClient.GetFromJsonAsync<InventoryTransferListResponse>($"api/inventorytransfer/{warehouseCode}/paged?page={page}&pageSize={pageSize}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<InventoryTransferDto?> GetTransferByDocEntryAsync(int docEntry)
    {
        try
        {
            // Use cache service
            return await _cacheService.GetCachedTransferByDocEntryAsync(docEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfer from cache, falling back to API");
            try
            {
                return await _httpClient.GetFromJsonAsync<InventoryTransferDto>($"api/inventorytransfer/doc/{docEntry}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<InventoryTransferDateResponse?> GetTransfersByDateAsync(string warehouseCode, DateTime date)
    {
        try
        {
            // Use cache for date query
            return await _cacheService.GetCachedTransfersByDateRangeAsync(warehouseCode, date, date);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfers by date from cache, falling back to API");
            try
            {
                var dateStr = date.ToString("yyyy-MM-dd");
                return await _httpClient.GetFromJsonAsync<InventoryTransferDateResponse>($"api/inventorytransfer/{warehouseCode}/date/{dateStr}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<InventoryTransferDateResponse?> GetTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate)
    {
        try
        {
            // Use cache for date range query
            return await _cacheService.GetCachedTransfersByDateRangeAsync(warehouseCode, fromDate, toDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfers by date range from cache, falling back to API");
            try
            {
                var from = fromDate.ToString("yyyy-MM-dd");
                var to = toDate.ToString("yyyy-MM-dd");
                return await _httpClient.GetFromJsonAsync<InventoryTransferDateResponse>($"api/inventorytransfer/{warehouseCode}/date/{from}/{to}");
            }
            catch
            {
                return null;
            }
        }
    }
}
