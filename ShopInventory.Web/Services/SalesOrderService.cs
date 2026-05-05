using ShopInventory.Web.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

        var message = ExtractApprovalErrorMessage(body, response.StatusCode);
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string ExtractApprovalErrorMessage(string responseBody, HttpStatusCode statusCode)
    {
        string? extractedMessage = null;

        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;

                if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in errorsElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var error in property.Value.EnumerateArray())
                        {
                            var message = error.GetString();
                            if (!string.IsNullOrWhiteSpace(message))
                            {
                                extractedMessage = message;
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(extractedMessage))
                        {
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(extractedMessage) && root.TryGetProperty("title", out var titleElement))
                {
                    var title = titleElement.GetString();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        extractedMessage = title;
                    }
                }

                if (string.IsNullOrWhiteSpace(extractedMessage) && root.TryGetProperty("message", out var messageElement))
                {
                    var message = messageElement.GetString();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        extractedMessage = message;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return "Your session has expired. Please sign in again and try approving the order.";
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            return "You do not have permission to approve sales orders.";
        }

        if (!string.IsNullOrWhiteSpace(extractedMessage))
        {
            return NormalizeApprovalErrorMessage(extractedMessage);
        }

        return "We couldn't approve this sales order right now. Please try again.";
    }

    private static string NormalizeApprovalErrorMessage(string message)
    {
        const string salesOrderPrefix = "Failed to approve sales order:";

        var normalizedMessage = message.Trim();
        if (!normalizedMessage.StartsWith(salesOrderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedMessage;
        }

        var reason = normalizedMessage[salesOrderPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "We couldn't approve this sales order right now. Please try again.";
        }

        var separatorIndex = reason.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            var orderNumber = reason[..separatorIndex].Trim();
            var detail = reason[(separatorIndex + 3)..].Trim();
            if (int.TryParse(orderNumber, out _) && !string.IsNullOrWhiteSpace(detail))
            {
                return $"Order {orderNumber} could not be approved because {ToSentenceFragment(detail)}.";
            }
        }

        return $"This sales order could not be approved because {ToSentenceFragment(reason)}.";
    }

    private static string ToSentenceFragment(string value)
    {
        var trimmedValue = value.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            return "the request could not be completed";
        }

        return char.ToLowerInvariant(trimmedValue[0]) + trimmedValue[1..];
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
