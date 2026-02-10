using Microsoft.Extensions.Logging;
using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public class PriceServiceResult<T>
{
    public bool IsSuccess { get; set; }
    public T? Data { get; set; }
    public string? ErrorMessage { get; set; }
    public int StatusCode { get; set; }
}

public interface IPriceService
{
    Task<ItemPricesResponse?> GetAllPricesAsync();
    Task<ItemPricesGroupedResponse?> GetGroupedPricesAsync();
    Task<PriceServiceResult<ItemPricesGroupedResponse>> GetGroupedPricesWithErrorAsync();
    Task<ItemPriceGroupedDto?> GetPriceByItemCodeAsync(string itemCode);
    Task<PriceListsResponse?> GetPriceListsAsync();
    Task<ItemPricesByListResponse?> GetPricesByPriceListAsync(int priceListNum);
    Task<ItemPriceByListDto?> GetItemPriceFromListAsync(int priceListNum, string itemCode);
}

public class PriceService : IPriceService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PriceService> _logger;

    public PriceService(HttpClient httpClient, ILogger<PriceService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ItemPricesResponse?> GetAllPricesAsync()
    {
        try
        {
            _logger.LogDebug("Fetching all prices from API: {BaseAddress}api/price", _httpClient.BaseAddress);
            _logger.LogDebug("Auth header: {Auth}", _httpClient.DefaultRequestHeaders.Authorization?.ToString() ?? "NOT SET");

            var response = await _httpClient.GetAsync("api/price");
            _logger.LogDebug("GetAllPricesAsync response: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to get prices: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ItemPricesResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching all prices");
            return null;
        }
    }

    public async Task<ItemPricesGroupedResponse?> GetGroupedPricesAsync()
    {
        var result = await GetGroupedPricesWithErrorAsync();
        return result.Data;
    }

    public async Task<PriceServiceResult<ItemPricesGroupedResponse>> GetGroupedPricesWithErrorAsync()
    {
        try
        {
            _logger.LogInformation("Fetching grouped prices from API: {BaseAddress}api/price/grouped", _httpClient.BaseAddress);
            _logger.LogInformation("Auth header: {Auth}", _httpClient.DefaultRequestHeaders.Authorization?.ToString() ?? "NOT SET");

            var response = await _httpClient.GetAsync("api/price/grouped");
            _logger.LogInformation("GetGroupedPricesAsync response: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to get grouped prices: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new PriceServiceResult<ItemPricesGroupedResponse>
                {
                    IsSuccess = false,
                    StatusCode = (int)response.StatusCode,
                    ErrorMessage = $"API returned {response.StatusCode}: {errorContent}"
                };
            }

            var data = await response.Content.ReadFromJsonAsync<ItemPricesGroupedResponse>();
            return new PriceServiceResult<ItemPricesGroupedResponse>
            {
                IsSuccess = true,
                Data = data,
                StatusCode = (int)response.StatusCode
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching grouped prices");
            return new PriceServiceResult<ItemPricesGroupedResponse>
            {
                IsSuccess = false,
                ErrorMessage = $"Exception: {ex.Message}"
            };
        }
    }

    public async Task<ItemPriceGroupedDto?> GetPriceByItemCodeAsync(string itemCode)
    {
        try
        {
            _logger.LogDebug("Fetching price for item {ItemCode} from API", itemCode);
            _logger.LogDebug("Auth header: {Auth}", _httpClient.DefaultRequestHeaders.Authorization?.ToString() ?? "NOT SET");

            // API endpoint is /api/price/{itemCode}
            var response = await _httpClient.GetAsync($"api/price/{Uri.EscapeDataString(itemCode)}");
            _logger.LogDebug("GetPriceByItemCodeAsync response: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to get price for {ItemCode}: {StatusCode} - {Error}", itemCode, response.StatusCode, errorContent);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ItemPriceGroupedDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching price for item {ItemCode}", itemCode);
            return null;
        }
    }

    public async Task<PriceListsResponse?> GetPriceListsAsync()
    {
        try
        {
            _logger.LogDebug("Fetching price lists from API: {BaseAddress}api/price/pricelists", _httpClient.BaseAddress);

            var response = await _httpClient.GetAsync("api/price/pricelists");
            _logger.LogDebug("GetPriceListsAsync response: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to get price lists: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PriceListsResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching price lists");
            return null;
        }
    }

    public async Task<ItemPricesByListResponse?> GetPricesByPriceListAsync(int priceListNum)
    {
        try
        {
            _logger.LogDebug("Fetching prices for price list {PriceListNum} from API", priceListNum);

            var response = await _httpClient.GetAsync($"api/price/pricelists/{priceListNum}/items");
            _logger.LogDebug("GetPricesByPriceListAsync response: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to get prices for list {PriceListNum}: {StatusCode} - {Error}", priceListNum, response.StatusCode, errorContent);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ItemPricesByListResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching prices for price list {PriceListNum}", priceListNum);
            return null;
        }
    }

    public async Task<ItemPriceByListDto?> GetItemPriceFromListAsync(int priceListNum, string itemCode)
    {
        try
        {
            _logger.LogDebug("Fetching price for item {ItemCode} from price list {PriceListNum}", itemCode, priceListNum);

            var response = await _httpClient.GetAsync($"api/price/pricelists/{priceListNum}/items/{Uri.EscapeDataString(itemCode)}");
            _logger.LogDebug("GetItemPriceFromListAsync response: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to get price for {ItemCode} from list {PriceListNum}: {StatusCode} - {Error}", itemCode, priceListNum, response.StatusCode, errorContent);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ItemPriceByListDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching price for item {ItemCode} from price list {PriceListNum}", itemCode, priceListNum);
            return null;
        }
    }
}
