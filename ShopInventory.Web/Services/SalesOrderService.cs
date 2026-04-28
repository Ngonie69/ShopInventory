using ShopInventory.Web.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Blazored.LocalStorage;

namespace ShopInventory.Web.Services;

public interface ISalesOrderService
{
    Task<SalesOrderListResponse?> GetSalesOrdersAsync(int page = 1, int pageSize = 20, SalesOrderStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, SalesOrderSource? source = null, string? search = null);
    Task<SalesOrderDto?> GetSalesOrderByIdAsync(int id);
    Task<SalesOrderDto?> GetLocalSalesOrderByIdAsync(int id);
    Task<SalesOrderDto?> GetSalesOrderByNumberAsync(string orderNumber);
    Task<SalesOrderDto?> GetSalesOrderDetailsAsync(SalesOrderDto order);
    Task<SalesOrderDto?> CreateSalesOrderAsync(CreateSalesOrderRequest request);
    Task<SalesOrderDto?> UpdateSalesOrderAsync(int id, CreateSalesOrderRequest request);
    Task<SalesOrderDto?> UpdateStatusAsync(int id, SalesOrderStatus status, string? comments = null);
    Task<SalesOrderDto?> ApproveAsync(int id);
    Task<InvoiceDto?> ConvertToInvoiceAsync(int id);
    Task<bool> DeleteSalesOrderAsync(int id);
    Task<SalesOrderDto?> PostToSAPAsync(int id);
}

public class SalesOrderService : ISalesOrderService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SalesOrderService> _logger;
    private readonly ILocalStorageService _localStorage;

    public SalesOrderService(HttpClient httpClient, ILogger<SalesOrderService> logger, ILocalStorageService localStorage)
    {
        _httpClient = httpClient;
        _logger = logger;
        _localStorage = localStorage;
    }

    private async Task EnsureAuthenticationAsync()
    {
        try
        {
            var token = await _localStorage.GetItemAsync<string>("authToken");
            var currentToken = _httpClient.DefaultRequestHeaders.Authorization?.Parameter;

            if (string.IsNullOrWhiteSpace(token))
            {
                _httpClient.DefaultRequestHeaders.Authorization = null;
                return;
            }

            if (!string.Equals(currentToken, token, StringComparison.Ordinal))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        catch
        {
            // localStorage not available during prerendering
        }
    }

    public async Task<SalesOrderListResponse?> GetSalesOrdersAsync(int page = 1, int pageSize = 20,
        SalesOrderStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, SalesOrderSource? source = null, string? search = null)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };

            if (status.HasValue)
                queryParams.Add($"status={(int)status.Value}");
            if (!string.IsNullOrEmpty(cardCode))
                queryParams.Add($"cardCode={Uri.EscapeDataString(cardCode)}");
            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (source.HasValue)
                queryParams.Add($"source={(int)source.Value}");
            if (!string.IsNullOrEmpty(search))
                queryParams.Add($"search={Uri.EscapeDataString(search)}");

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
            await EnsureAuthenticationAsync();
            return await _httpClient.GetFromJsonAsync<SalesOrderDto>($"api/salesorder/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales order {Id}", id);
            return null;
        }
    }

    public async Task<SalesOrderDto?> GetLocalSalesOrderByIdAsync(int id)
    {
        try
        {
            await EnsureAuthenticationAsync();
            return await _httpClient.GetFromJsonAsync<SalesOrderDto>($"api/salesorder/local/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching local sales order {Id}", id);
            return null;
        }
    }

    public async Task<SalesOrderDto?> GetSalesOrderByNumberAsync(string orderNumber)
    {
        try
        {
            await EnsureAuthenticationAsync();
            return await _httpClient.GetFromJsonAsync<SalesOrderDto>($"api/salesorder/number/{Uri.EscapeDataString(orderNumber)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales order by number {OrderNumber}", orderNumber);
            return null;
        }
    }

    public async Task<SalesOrderDto?> GetSalesOrderDetailsAsync(SalesOrderDto order)
    {
        if (ShouldLoadFromLocal(order))
        {
            var localOrder = await GetLocalSalesOrderByIdAsync(order.Id);
            if (localOrder != null)
                return localOrder;

            if (!string.IsNullOrWhiteSpace(order.OrderNumber))
                return await GetSalesOrderByNumberAsync(order.OrderNumber);
        }

        var sapDocEntry = order.SAPDocEntry ?? order.Id;
        return await GetSalesOrderByIdAsync(sapDocEntry);
    }

    private static bool ShouldLoadFromLocal(SalesOrderDto order)
    {
        return !order.IsSynced ||
            (!string.IsNullOrWhiteSpace(order.OrderNumber) &&
                !order.OrderNumber.StartsWith("SAP-", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SalesOrderDto?> CreateSalesOrderAsync(CreateSalesOrderRequest request)
    {
        await EnsureAuthenticationAsync();
        var response = await _httpClient.PostAsJsonAsync("api/salesorder", request);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
        }

        var errorBody = await response.Content.ReadAsStringAsync();
        _logger.LogWarning("Failed to create sales order: {StatusCode} - {Error}", response.StatusCode, errorBody);
        throw new HttpRequestException($"Server returned {(int)response.StatusCode}: {errorBody}");
    }

    public async Task<SalesOrderDto?> UpdateSalesOrderAsync(int id, CreateSalesOrderRequest request)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.PutAsJsonAsync($"api/salesorder/{id}", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
            }
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                throw new HttpRequestException("This order was modified by another user. Please reload and try again.");
            }
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to update sales order {Id}: {StatusCode} - {Error}", id, response.StatusCode, errorBody);
            throw new HttpRequestException($"Server returned {(int)response.StatusCode}: {errorBody}");
        }
        catch (HttpRequestException)
        {
            throw;
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
            await EnsureAuthenticationAsync();
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
        await EnsureAuthenticationAsync();
        var response = await _httpClient.PostAsync($"api/salesorder/{id}/approve", null);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
        }

        var body = await response.Content.ReadAsStringAsync();
        _logger.LogWarning("Failed to approve sales order {Id}: {StatusCode} - {Body}", id, response.StatusCode, body);

        var message = response.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? "You do not have permission to approve sales orders."
            : $"Approval failed ({(int)response.StatusCode}): {(body.Length > 200 ? body[..200] : body)}";
        throw new HttpRequestException(message);
    }

    public async Task<InvoiceDto?> ConvertToInvoiceAsync(int id)
    {
        try
        {
            await EnsureAuthenticationAsync(); var response = await _httpClient.PostAsync($"api/salesorder/{id}/convert-to-invoice", null);
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
            await EnsureAuthenticationAsync();
            var response = await _httpClient.DeleteAsync($"api/salesorder/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting sales order {Id}", id);
            return false;
        }
    }

    public async Task<SalesOrderDto?> PostToSAPAsync(int id)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.PostAsync($"api/salesorder/{id}/post-to-sap", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
            }
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to post sales order to SAP {Id}: {StatusCode} - {Error}", id, response.StatusCode, errorBody);
            throw new HttpRequestException($"Server returned {(int)response.StatusCode}: {errorBody}");
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting sales order to SAP {Id}", id);
            throw;
        }
    }
}
