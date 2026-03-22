using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IQuotationService
{
    Task<QuotationListResponse?> GetQuotationsAsync(int page = 1, int pageSize = 20, QuotationStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null);
    Task<QuotationListResponse?> GetQuotationsFromSAPAsync(int page = 1, int pageSize = 20, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null);
    Task<QuotationDto?> GetQuotationByIdAsync(int id);
    Task<QuotationDto?> GetQuotationByNumberAsync(string quotationNumber);
    Task<QuotationDto?> CreateQuotationAsync(CreateQuotationRequest request);
    Task<QuotationDto?> UpdateQuotationAsync(int id, CreateQuotationRequest request);
    Task<QuotationDto?> UpdateStatusAsync(int id, QuotationStatus status, string? comments = null);
    Task<QuotationDto?> ApproveAsync(int id);
    Task<SalesOrderDto?> ConvertToSalesOrderAsync(int id);
    Task<bool> DeleteQuotationAsync(int id);
}

public class QuotationService : IQuotationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QuotationService> _logger;

    public QuotationService(HttpClient httpClient, ILogger<QuotationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<QuotationListResponse?> GetQuotationsAsync(int page = 1, int pageSize = 20,
        QuotationStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null)
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

            var url = $"api/quotation?{string.Join("&", queryParams)}";
            _logger.LogInformation("Fetching quotations from API: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("API returned error: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<QuotationListResponse>(content, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _logger.LogInformation("Deserialized {Count} quotations, TotalCount: {TotalCount}",
                result?.Quotations?.Count ?? 0, result?.TotalCount ?? 0);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quotations");
            return null;
        }
    }

    public async Task<QuotationListResponse?> GetQuotationsFromSAPAsync(int page = 1, int pageSize = 20,
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

            var url = $"api/quotation/sap?{string.Join("&", queryParams)}";
            _logger.LogInformation("Fetching quotations from SAP API: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("SAP API returned error: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<QuotationListResponse>(content, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _logger.LogInformation("Fetched {Count} quotations from SAP", result?.Quotations?.Count ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quotations from SAP");
            return null;
        }
    }

    public async Task<QuotationDto?> GetQuotationByIdAsync(int id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<QuotationDto>($"api/quotation/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quotation {Id}", id);
            return null;
        }
    }

    public async Task<QuotationDto?> GetQuotationByNumberAsync(string quotationNumber)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<QuotationDto>($"api/quotation/number/{Uri.EscapeDataString(quotationNumber)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quotation by number {QuotationNumber}", quotationNumber);
            return null;
        }
    }

    public async Task<QuotationDto?> CreateQuotationAsync(CreateQuotationRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync("api/quotation", request);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<QuotationDto>();
        }

        var errorBody = await response.Content.ReadAsStringAsync();
        _logger.LogWarning("Failed to create quotation: {StatusCode} - {Error}", response.StatusCode, errorBody);
        throw new HttpRequestException($"Server returned {(int)response.StatusCode}: {errorBody}");
    }

    public async Task<QuotationDto?> UpdateQuotationAsync(int id, CreateQuotationRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"api/quotation/{id}", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<QuotationDto>();
            }
            _logger.LogWarning("Failed to update quotation {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating quotation {Id}", id);
            return null;
        }
    }

    public async Task<QuotationDto?> UpdateStatusAsync(int id, QuotationStatus status, string? comments = null)
    {
        try
        {
            var request = new UpdateQuotationStatusRequest { Status = status, Comments = comments };
            var response = await _httpClient.PatchAsJsonAsync($"api/quotation/{id}/status", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<QuotationDto>();
            }
            _logger.LogWarning("Failed to update quotation status {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating quotation status {Id}", id);
            return null;
        }
    }

    public async Task<QuotationDto?> ApproveAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/quotation/{id}/approve", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<QuotationDto>();
            }
            _logger.LogWarning("Failed to approve quotation {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving quotation {Id}", id);
            return null;
        }
    }

    public async Task<SalesOrderDto?> ConvertToSalesOrderAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/quotation/{id}/convert-to-sales-order", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SalesOrderDto>();
            }
            _logger.LogWarning("Failed to convert quotation to sales order {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting quotation to sales order {Id}", id);
            return null;
        }
    }

    public async Task<bool> DeleteQuotationAsync(int id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/quotation/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting quotation {Id}", id);
            return false;
        }
    }
}
