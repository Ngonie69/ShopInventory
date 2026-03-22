using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IPurchaseOrderService
{
    Task<PurchaseOrderListResponse?> GetPurchaseOrdersAsync(int page = 1, int pageSize = 20, PurchaseOrderStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null);
    Task<PurchaseOrderListResponse?> GetPurchaseOrdersFromSAPAsync(int page = 1, int pageSize = 20, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null);
    Task<PurchaseOrderDto?> GetPurchaseOrderByIdAsync(int id);
    Task<PurchaseOrderDto?> GetPurchaseOrderByNumberAsync(string orderNumber);
    Task<PurchaseOrderDto?> CreatePurchaseOrderAsync(CreatePurchaseOrderRequest request);
    Task<PurchaseOrderDto?> UpdatePurchaseOrderAsync(int id, CreatePurchaseOrderRequest request);
    Task<PurchaseOrderDto?> UpdateStatusAsync(int id, PurchaseOrderStatus status, string? comments = null);
    Task<PurchaseOrderDto?> ApproveAsync(int id);
    Task<PurchaseOrderDto?> ReceiveItemsAsync(int id, ReceivePurchaseOrderRequest request);
    Task<bool> DeletePurchaseOrderAsync(int id);
}

public class PurchaseOrderService : IPurchaseOrderService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PurchaseOrderService> _logger;

    public PurchaseOrderService(HttpClient httpClient, ILogger<PurchaseOrderService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PurchaseOrderListResponse?> GetPurchaseOrdersAsync(int page = 1, int pageSize = 20,
        PurchaseOrderStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };

            if (status.HasValue)
                queryParams.Add($"status={(int)status.Value}");
            if (!string.IsNullOrEmpty(cardCode))
                queryParams.Add($"cardCode={Uri.EscapeDataString(cardCode)}");
            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");

            var url = $"api/purchaseorder?{string.Join("&", queryParams)}";
            _logger.LogInformation("Fetching purchase orders from API: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("API Response Status: {StatusCode}, Content Length: {Length}",
                response.StatusCode, content.Length);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("API returned error: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<PurchaseOrderListResponse>(content, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _logger.LogInformation("Deserialized {OrderCount} purchase orders, TotalCount: {TotalCount}",
                result?.Orders?.Count ?? 0, result?.TotalCount ?? 0);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching purchase orders");
            return null;
        }
    }

    public async Task<PurchaseOrderListResponse?> GetPurchaseOrdersFromSAPAsync(int page = 1, int pageSize = 20,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };

            if (!string.IsNullOrEmpty(cardCode))
                queryParams.Add($"cardCode={Uri.EscapeDataString(cardCode)}");
            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");

            var url = $"api/purchaseorder/sap?{string.Join("&", queryParams)}";
            _logger.LogInformation("Fetching purchase orders from SAP API: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("SAP API returned error: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<PurchaseOrderListResponse>(content, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _logger.LogInformation("Fetched {Count} purchase orders from SAP", result?.Orders?.Count ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching purchase orders from SAP");
            return null;
        }
    }

    public async Task<PurchaseOrderDto?> GetPurchaseOrderByIdAsync(int id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<PurchaseOrderDto>($"api/purchaseorder/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching purchase order {Id}", id);
            return null;
        }
    }

    public async Task<PurchaseOrderDto?> GetPurchaseOrderByNumberAsync(string orderNumber)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<PurchaseOrderDto>($"api/purchaseorder/number/{Uri.EscapeDataString(orderNumber)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching purchase order by number {OrderNumber}", orderNumber);
            return null;
        }
    }

    public async Task<PurchaseOrderDto?> CreatePurchaseOrderAsync(CreatePurchaseOrderRequest request)
    {
        try
        {
            _logger.LogInformation("Creating purchase order for supplier {CardCode} with {LineCount} lines",
                request.CardCode, request.Lines?.Count ?? 0);

            var response = await _httpClient.PostAsJsonAsync("api/purchaseorder", request);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<PurchaseOrderDto>(content,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _logger.LogInformation("Created purchase order {OrderNumber}", result?.OrderNumber);
                return result;
            }

            _logger.LogWarning("Failed to create purchase order: {StatusCode} - {Error}", response.StatusCode, content);
            throw new HttpRequestException($"API returned {(int)response.StatusCode}: {content}");
        }
        catch (HttpRequestException)
        {
            throw; // Re-throw HTTP errors with API details
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating purchase order");
            throw;
        }
    }

    public async Task<PurchaseOrderDto?> UpdatePurchaseOrderAsync(int id, CreatePurchaseOrderRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/purchaseorder/{id}", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PurchaseOrderDto>();
            }
            _logger.LogWarning("Failed to update purchase order {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating purchase order {Id}", id);
            return null;
        }
    }

    public async Task<PurchaseOrderDto?> UpdateStatusAsync(int id, PurchaseOrderStatus status, string? comments = null)
    {
        try
        {
            var request = new UpdatePurchaseOrderStatusRequest { Status = status, Comments = comments };
            var response = await _httpClient.PatchAsJsonAsync($"api/purchaseorder/{id}/status", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PurchaseOrderDto>();
            }
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to update purchase order status {Id}: {StatusCode} - {Error}", id, response.StatusCode, errorContent);
            throw new HttpRequestException($"Failed to update status: {(int)response.StatusCode}");
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating purchase order status {Id}", id);
            throw;
        }
    }

    public async Task<PurchaseOrderDto?> ApproveAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/purchaseorder/{id}/approve", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PurchaseOrderDto>();
            }
            _logger.LogWarning("Failed to approve purchase order {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving purchase order {Id}", id);
            return null;
        }
    }

    public async Task<PurchaseOrderDto?> ReceiveItemsAsync(int id, ReceivePurchaseOrderRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/purchaseorder/{id}/receive", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<PurchaseOrderDto>();
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to receive items for purchase order {Id}: {StatusCode} - {Error}", id, response.StatusCode, errorContent);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving items for purchase order {Id}", id);
            return null;
        }
    }

    public async Task<bool> DeletePurchaseOrderAsync(int id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/purchaseorder/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting purchase order {Id}", id);
            return false;
        }
    }
}
