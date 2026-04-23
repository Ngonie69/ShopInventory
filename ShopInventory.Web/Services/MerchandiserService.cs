using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IMerchandiserService
{
    Task<List<MerchandiserSummaryDto>> GetMerchandisersAsync();
    Task<MerchandiserProductListResponse?> GetMerchandiserProductsAsync(Guid userId);
    Task<MerchandiserProductListResponse> GetGlobalProductsAsync(CancellationToken cancellationToken = default);
    Task<List<SapSalesItemDto>> GetSapSalesItemsAsync();
    Task<MerchandiserProductListResponse?> AssignProductsAsync(Guid userId, List<string> itemCodes);
    Task<MerchandiserProductListResponse?> AssignProductsGlobalAsync(List<string> itemCodes, Dictionary<string, string>? itemNames = null);
    Task UpdateProductStatusAsync(Guid userId, List<string> itemCodes, bool isActive);
    Task UpdateProductStatusGlobalAsync(List<string> itemCodes, bool isActive);
    Task RemoveProductsGlobalAsync(List<string> itemCodes);
    Task RemoveProductsAsync(Guid userId, List<string> itemCodes);
    Task<int> BackfillProductDetailsAsync(CancellationToken cancellationToken = default);
}

public class MerchandiserService : IMerchandiserService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MerchandiserService> _logger;

    public MerchandiserService(HttpClient httpClient, ILogger<MerchandiserService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<MerchandiserSummaryDto>> GetMerchandisersAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<MerchandiserSummaryDto>>("api/merchandiser") ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching merchandisers");
            return new();
        }
    }

    public async Task<MerchandiserProductListResponse?> GetMerchandiserProductsAsync(Guid userId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<MerchandiserProductListResponse>($"api/merchandiser/{userId}/products");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching merchandiser products for {UserId}", userId);
            return null;
        }
    }

    public async Task<MerchandiserProductListResponse> GetGlobalProductsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("api/merchandiser/products", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to fetch global merchandiser products: {error}");
        }

        return await response.Content.ReadFromJsonAsync<MerchandiserProductListResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Global merchandiser products response was empty.");
    }

    public async Task<List<SapSalesItemDto>> GetSapSalesItemsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<SapSalesItemDto>>("api/merchandiser/sap-sales-items") ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching SAP sales items");
            return new();
        }
    }

    public async Task<MerchandiserProductListResponse?> AssignProductsAsync(Guid userId, List<string> itemCodes)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/merchandiser/{userId}/products", new { ItemCodes = itemCodes });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to assign products: {error}");
        }
        return await response.Content.ReadFromJsonAsync<MerchandiserProductListResponse>();
    }

    public async Task<MerchandiserProductListResponse?> AssignProductsGlobalAsync(List<string> itemCodes, Dictionary<string, string>? itemNames = null)
    {
        var response = await _httpClient.PostAsJsonAsync("api/merchandiser/products", new { ItemCodes = itemCodes, ItemNames = itemNames });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to assign products globally: {error}");
        }
        return await response.Content.ReadFromJsonAsync<MerchandiserProductListResponse>();
    }

    public async Task UpdateProductStatusAsync(Guid userId, List<string> itemCodes, bool isActive)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/merchandiser/{userId}/products/status", new
        {
            ItemCodes = itemCodes,
            IsActive = isActive
        });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to update product status: {error}");
        }
    }

    public async Task UpdateProductStatusGlobalAsync(List<string> itemCodes, bool isActive)
    {
        var response = await _httpClient.PutAsJsonAsync("api/merchandiser/products/status", new
        {
            ItemCodes = itemCodes,
            IsActive = isActive
        });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to update product status globally: {error}");
        }
    }

    public async Task RemoveProductsGlobalAsync(List<string> itemCodes)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "api/merchandiser/products")
        {
            Content = JsonContent.Create(new { ItemCodes = itemCodes })
        };
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to remove products globally: {error}");
        }
    }

    public async Task RemoveProductsAsync(Guid userId, List<string> itemCodes)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"api/merchandiser/{userId}/products")
        {
            Content = JsonContent.Create(new { ItemCodes = itemCodes })
        };
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to remove products: {error}");
        }
    }

    public async Task<int> BackfillProductDetailsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("api/merchandiser/backfill-product-details", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Failed to backfill product details: {error}");
        }
        var result = await response.Content.ReadFromJsonAsync<BackfillResult>(cancellationToken: cancellationToken);
        return result?.Updated ?? 0;
    }

    private record BackfillResult(int Updated);
}
