using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface ISalesOrderService
{
    Task<SalesOrderListResponse?> GetSalesOrdersAsync(int page = 1, int pageSize = 20, SalesOrderStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null);
    Task<SalesOrderDto?> GetSalesOrderByIdAsync(int id);
    Task<SalesOrderDto?> GetSalesOrderByNumberAsync(string orderNumber);
    Task<SalesOrderDto?> CreateSalesOrderAsync(CreateSalesOrderRequest request);
    Task<SalesOrderDto?> UpdateSalesOrderAsync(int id, CreateSalesOrderRequest request);
    Task<SalesOrderDto?> UpdateStatusAsync(int id, SalesOrderStatus status, string? comments = null);
    Task<SalesOrderDto?> ApproveAsync(int id);
    Task<InvoiceDto?> ConvertToInvoiceAsync(int id);
    Task<bool> DeleteSalesOrderAsync(int id);
}

public class SalesOrderService : ISalesOrderService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SalesOrderService> _logger;

    public SalesOrderService(HttpClient httpClient, ILogger<SalesOrderService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SalesOrderListResponse?> GetSalesOrdersAsync(int page = 1, int pageSize = 20,
        SalesOrderStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null)
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

            var url = $"api/salesorder?{string.Join("&", queryParams)}";
            _logger.LogInformation("Fetching sales orders from API: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("API Response Status: {StatusCode}, Content Length: {Length}",
                response.StatusCode, content.Length);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("API returned error: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            _logger.LogDebug("API Response Content: {Content}", content.Length > 500 ? content.Substring(0, 500) + "..." : content);

            var result = System.Text.Json.JsonSerializer.Deserialize<SalesOrderListResponse>(content, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _logger.LogInformation("Deserialized {OrderCount} orders, TotalCount: {TotalCount}",
                result?.Orders?.Count ?? 0, result?.TotalCount ?? 0);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales orders");
            return null;
        }
    }

    public async Task<SalesOrderDto?> GetSalesOrderByIdAsync(int id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SalesOrderDto>($"api/salesorder/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales order {Id}", id);
            return null;
        }
    }

    public async Task<SalesOrderDto?> GetSalesOrderByNumberAsync(string orderNumber)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SalesOrderDto>($"api/salesorder/number/{Uri.EscapeDataString(orderNumber)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales order by number {OrderNumber}", orderNumber);
            return null;
        }
    }

    public async Task<SalesOrderDto?> CreateSalesOrderAsync(CreateSalesOrderRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/salesorder", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
            }
            _logger.LogWarning("Failed to create sales order: {StatusCode}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating sales order");
            return null;
        }
    }

    public async Task<SalesOrderDto?> UpdateSalesOrderAsync(int id, CreateSalesOrderRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/salesorder/{id}", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
            }
            _logger.LogWarning("Failed to update sales order {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating sales order {Id}", id);
            return null;
        }
    }

    public async Task<SalesOrderDto?> UpdateStatusAsync(int id, SalesOrderStatus status, string? comments = null)
    {
        try
        {
            var request = new UpdateSalesOrderStatusRequest { Status = status, Comments = comments };
            var response = await _httpClient.PatchAsJsonAsync($"api/salesorder/{id}/status", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
            }
            _logger.LogWarning("Failed to update sales order status {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating sales order status {Id}", id);
            return null;
        }
    }

    public async Task<SalesOrderDto?> ApproveAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/salesorder/{id}/approve", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
            }
            _logger.LogWarning("Failed to approve sales order {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving sales order {Id}", id);
            return null;
        }
    }

    public async Task<InvoiceDto?> ConvertToInvoiceAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/salesorder/{id}/convert-to-invoice", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<InvoiceDto>();
            }
            _logger.LogWarning("Failed to convert sales order to invoice {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting sales order to invoice {Id}", id);
            return null;
        }
    }

    public async Task<bool> DeleteSalesOrderAsync(int id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/salesorder/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting sales order {Id}", id);
            return false;
        }
    }
}
