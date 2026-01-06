using ShopInventory.Web.Models;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ShopInventory.Web.Services;

public interface IInvoiceService
{
    Task<InvoiceListResponse?> GetInvoicesAsync(int page = 1, int pageSize = 20);
    Task<InvoiceDto?> GetInvoiceByDocEntryAsync(int docEntry);
    Task<InvoiceDateResponse?> GetInvoicesByCustomerAsync(string cardCode);
    Task<InvoiceDateResponse?> GetInvoicesByDateAsync(DateTime date);
    Task<InvoiceDateResponse?> GetInvoicesByDateRangeAsync(DateTime fromDate, DateTime toDate);
    Task<(bool Success, string Message, InvoiceDto? Invoice)> CreateInvoiceAsync(CreateInvoiceRequest request);
}

public class InvoiceService : IInvoiceService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(HttpClient httpClient, ILogger<InvoiceService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<InvoiceListResponse?> GetInvoicesAsync(int page = 1, int pageSize = 20)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InvoiceListResponse>($"api/invoice/paged?page={page}&pageSize={pageSize}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<InvoiceDto?> GetInvoiceByDocEntryAsync(int docEntry)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InvoiceDto>($"api/invoice/{docEntry}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<InvoiceDateResponse?> GetInvoicesByCustomerAsync(string cardCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InvoiceDateResponse>($"api/invoice/customer/{cardCode}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<InvoiceDateResponse?> GetInvoicesByDateAsync(DateTime date)
    {
        try
        {
            // Use date-range endpoint with same date for from and to
            var dateStr = date.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<InvoiceDateResponse>($"api/invoice/date-range?fromDate={dateStr}&toDate={dateStr}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<InvoiceDateResponse?> GetInvoicesByDateRangeAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            var from = fromDate.ToString("yyyy-MM-dd");
            var to = toDate.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<InvoiceDateResponse>($"api/invoice/date-range?fromDate={from}&toDate={to}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Success, string Message, InvoiceDto? Invoice)> CreateInvoiceAsync(CreateInvoiceRequest request)
    {
        try
        {
            _logger.LogInformation("Sending invoice creation request for customer {CardCode} with {LineCount} lines",
                request.CardCode, request.Lines.Count);

            // Log the full request as JSON for debugging
            var requestJson = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            _logger.LogDebug("Invoice request payload:\n{RequestJson}", requestJson);

            // Use auto-allocation with FEFO (First Expiry First Out) strategy
            // This ensures batches are automatically selected based on expiry dates
            var response = await _httpClient.PostAsJsonAsync("api/invoice?autoAllocateBatches=true&allocationStrategy=FEFO", request);

            _logger.LogInformation("Invoice API response: {StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<InvoiceCreatedResponse>();
                _logger.LogInformation("Invoice created successfully: DocNum={DocNum}, DocEntry={DocEntry}",
                    result?.Invoice?.DocNum, result?.Invoice?.DocEntry);
                return (true, result?.Message ?? "Invoice created successfully", result?.Invoice);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Invoice creation failed. Status: {StatusCode}, Response:\n{ErrorContent}",
                response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var errorMessage = errorResponse?.Message ?? "Failed to create invoice";

                // If there are multiple errors, join them
                if (errorResponse?.Errors?.Any() == true)
                {
                    errorMessage = string.Join("; ", errorResponse.Errors);
                }

                // Check for SAP-specific error structure
                if (errorContent.Contains("\"error\""))
                {
                    // Try to extract SAP error message
                    using var doc = System.Text.Json.JsonDocument.Parse(errorContent);
                    if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    {
                        if (errorProp.TryGetProperty("message", out var msgProp))
                        {
                            if (msgProp.TryGetProperty("value", out var valueProp))
                            {
                                errorMessage = valueProp.GetString() ?? errorMessage;
                            }
                            else if (msgProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                errorMessage = msgProp.GetString() ?? errorMessage;
                            }
                        }
                    }
                }

                return (false, errorMessage, null);
            }
            catch (Exception parseEx)
            {
                _logger.LogWarning(parseEx, "Failed to parse error response");
                return (false, $"Failed to create invoice: {response.StatusCode} - {errorContent}", null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error creating invoice. Status: {StatusCode}", httpEx.StatusCode);
            return (false, $"Network error: {httpEx.Message}", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating invoice");
            return (false, $"Error: {ex.Message}", null);
        }
    }
}
