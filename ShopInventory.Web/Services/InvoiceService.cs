using ShopInventory.Web.Models;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ShopInventory.Web.Services;

public interface IInvoiceService
{
    Task<InvoiceListResponse?> GetInvoicesAsync(int page = 1, int pageSize = 20);
    Task<InvoiceDto?> GetInvoiceByDocEntryAsync(int docEntry);
    Task<InvoiceDto?> GetInvoiceByDocNumAsync(int docNum);
    Task<InvoiceDateResponse?> GetInvoicesByCustomerAsync(string cardCode, DateTime? fromDate = null, DateTime? toDate = null);
    Task<InvoiceDateResponse?> GetInvoicesByDateAsync(DateTime date);
    Task<InvoiceDateResponse?> GetInvoicesByDateRangeAsync(DateTime fromDate, DateTime toDate);
    Task<(bool Success, string Message, InvoiceDto? Invoice, FiscalizationResult? Fiscalization)> CreateInvoiceAsync(CreateInvoiceRequest request);
    Task<byte[]?> GetInvoicePdfAsync(int docEntry);
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

    public async Task<InvoiceDto?> GetInvoiceByDocNumAsync(int docNum)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InvoiceDto>($"api/invoice/by-docnum/{docNum}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<InvoiceDateResponse?> GetInvoicesByCustomerAsync(string cardCode, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var url = $"api/invoice/customer/{Uri.EscapeDataString(cardCode)}";
            var queryParams = new List<string>();
            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (queryParams.Count > 0)
                url += "?" + string.Join("&", queryParams);
            return await _httpClient.GetFromJsonAsync<InvoiceDateResponse>(url);
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

    public async Task<(bool Success, string Message, InvoiceDto? Invoice, FiscalizationResult? Fiscalization)> CreateInvoiceAsync(CreateInvoiceRequest request)
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
                return (true, result?.Message ?? "Invoice created successfully", result?.Invoice, result?.Fiscalization);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Invoice creation failed. Status: {StatusCode}, Response:\n{ErrorContent}",
                response.StatusCode, errorContent);

            try
            {
                // Try to parse as batch validation error first (rich error structure)
                var friendlyBatchError = TryParseBatchValidationError(errorContent);
                if (friendlyBatchError != null)
                {
                    return (false, friendlyBatchError, null, null);
                }

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

                return (false, errorMessage, null, null);
            }
            catch (Exception parseEx)
            {
                _logger.LogWarning(parseEx, "Failed to parse error response");
                return (false, $"Failed to create invoice: {response.StatusCode} - {errorContent}", null, null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error creating invoice. Status: {StatusCode}", httpEx.StatusCode);
            return (false, $"Network error: {httpEx.Message}", null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating invoice");
            return (false, $"Error: {ex.Message}", null, null);
        }
    }

    /// <summary>
    /// Attempts to parse a batch validation error response into a user-friendly HTML message.
    /// Returns null if the response is not a batch validation error.
    /// </summary>
    private string? TryParseBatchValidationError(string errorContent)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(errorContent);
            var root = doc.RootElement;

            // Check if this is a batch validation error
            if (!root.TryGetProperty("isValid", out var isValidProp) || isValidProp.GetBoolean())
                return null;

            if (!root.TryGetProperty("errors", out var errorsArray) || errorsArray.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            var sb = new System.Text.StringBuilder();
            sb.Append("<strong>Some items don't have enough stock to complete this invoice:</strong>");
            sb.Append("<ul style='margin:8px 0 4px 0; padding-left:20px;'>");

            foreach (var err in errorsArray.EnumerateArray())
            {
                var itemCode = err.TryGetProperty("itemCode", out var ic) ? ic.GetString() : "Unknown";
                var warehouse = err.TryGetProperty("warehouseCode", out var wc) ? wc.GetString() : "";
                var requested = err.TryGetProperty("requestedQuantity", out var rq) ? rq.GetDecimal() : 0;
                var available = err.TryGetProperty("availableQuantity", out var aq) ? aq.GetDecimal() : 0;
                var suggestion = err.TryGetProperty("suggestedAction", out var sa) ? sa.GetString() : null;

                sb.Append("<li style='margin-bottom:6px;'>");
                sb.Append($"<strong>{System.Net.WebUtility.HtmlEncode(itemCode)}</strong>");

                if (!string.IsNullOrEmpty(warehouse))
                    sb.Append($" (warehouse: {System.Net.WebUtility.HtmlEncode(warehouse)})");

                sb.Append($" &mdash; You requested <strong>{requested:G29}</strong>");

                if (available > 0)
                    sb.Append($", but only <strong>{available:G29}</strong> is available");
                else
                    sb.Append(", but <strong>none</strong> is available");

                sb.Append('.');

                // Show suggested action in friendly terms
                if (!string.IsNullOrEmpty(suggestion))
                {
                    // Clean up the API-style suggestion to be more user-friendly
                    var friendlySuggestion = suggestion
                        .Replace("Reduce quantity to ", "Try reducing the quantity to ")
                        .Replace(" or transfer more stock", ", or ask for a stock transfer.");
                    sb.Append($"<br/><em style='color:#666; font-size:0.92em;'>{System.Net.WebUtility.HtmlEncode(friendlySuggestion)}</em>");
                }

                sb.Append("</li>");
            }

            sb.Append("</ul>");
            sb.Append("<span style='font-size:0.92em; color:#666;'>Please adjust the quantities and try again.</span>");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error content is not a batch validation response");
            return null;
        }
    }

    public async Task<byte[]?> GetInvoicePdfAsync(int docEntry)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/invoice/{docEntry}/pdf");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }

            _logger.LogWarning("Failed to download invoice PDF for DocEntry {DocEntry}: {StatusCode}",
                docEntry, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading invoice PDF for DocEntry {DocEntry}", docEntry);
            return null;
        }
    }
}
