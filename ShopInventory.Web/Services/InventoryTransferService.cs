using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IInventoryTransferService
{
    Task<InventoryTransferListResponse?> GetTransfersByWarehouseAsync(string warehouseCode);
    Task<InventoryTransferListResponse?> GetPagedTransfersAsync(string warehouseCode, int page = 1, int pageSize = 20);
    Task<InventoryTransferDto?> GetTransferByDocEntryAsync(int docEntry);
    Task<InventoryTransferDateResponse?> GetTransfersByDateAsync(string warehouseCode, DateTime date);
    Task<InventoryTransferDateResponse?> GetTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate, int? page = null, int? pageSize = null);

    // Inventory Transfer operations
    Task<(bool Success, string Message, InventoryTransferDto? Transfer)> CreateInventoryTransferAsync(CreateInventoryTransferDto request);

    // Transfer Request operations
    Task<(bool Success, string Message, InventoryTransferRequestDto? TransferRequest)> CreateTransferRequestAsync(CreateTransferRequestDto request);
    Task<TransferRequestListResponse?> GetTransferRequestsAsync(int page = 1, int pageSize = 20);
    Task<TransferRequestListResponse?> GetTransferRequestsByWarehouseAsync(string warehouseCode);
    Task<InventoryTransferRequestDto?> GetTransferRequestByDocEntryAsync(int docEntry);

    // Transfer Request conversion
    Task<(bool Success, string Message, InventoryTransferDto? Transfer)> ConvertTransferRequestToTransferAsync(int docEntry);
    Task<(bool Success, string Message)> CloseTransferRequestAsync(int docEntry);
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
        catch (TimeoutException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfers from cache, falling back to API");
            try
            {
                return await _httpClient.GetFromJsonAsync<InventoryTransferListResponse>($"api/inventorytransfer/{warehouseCode}");
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback API call also failed for warehouse {WarehouseCode}", warehouseCode);
                throw new Exception($"Unable to load transfers for warehouse {warehouseCode}. Please check if the API server is running.", fallbackEx);
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
        catch (TimeoutException)
        {
            throw; // Propagate timeout for UI handling
        }
        catch (HttpRequestException)
        {
            throw; // Propagate HTTP errors for UI handling
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting paged transfers from cache, falling back to API");
            try
            {
                return await _httpClient.GetFromJsonAsync<InventoryTransferListResponse>($"api/inventorytransfer/{warehouseCode}/paged?page={page}&pageSize={pageSize}");
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback API call also failed for warehouse {WarehouseCode}", warehouseCode);
                throw new Exception($"Unable to load transfers for warehouse {warehouseCode}. Please check if the API server is running.", fallbackEx);
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

    public async Task<InventoryTransferDateResponse?> GetTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate, int? page = null, int? pageSize = null)
    {
        try
        {
            if (page.HasValue || pageSize.HasValue)
            {
                var currentPage = Math.Max(page ?? 1, 1);
                var currentPageSize = Math.Clamp(pageSize ?? 20, 1, 100);
                var from = fromDate.ToString("yyyy-MM-dd");
                var to = toDate.ToString("yyyy-MM-dd");
                return await _httpClient.GetFromJsonAsync<InventoryTransferDateResponse>($"api/inventorytransfer/{warehouseCode}/daterange?fromDate={from}&toDate={to}&page={currentPage}&pageSize={currentPageSize}");
            }

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
                return await _httpClient.GetFromJsonAsync<InventoryTransferDateResponse>($"api/inventorytransfer/{warehouseCode}/daterange?fromDate={from}&toDate={to}");
            }
            catch
            {
                return null;
            }
        }
    }

    #region Inventory Transfer Operations

    public async Task<(bool Success, string Message, InventoryTransferDto? Transfer)> CreateInventoryTransferAsync(CreateInventoryTransferDto request)
    {
        try
        {
            _logger.LogInformation("Creating inventory transfer from {FromWarehouse} to {ToWarehouse} with {LineCount} lines",
                request.FromWarehouse, request.ToWarehouse, request.Lines.Count);

            var response = await _httpClient.PostAsJsonAsync("api/inventorytransfer", request);

            _logger.LogInformation("Inventory transfer API response: {StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<InventoryTransferCreatedResponse>();
                _logger.LogInformation("Inventory transfer created successfully: DocNum={DocNum}, DocEntry={DocEntry}",
                    result?.Transfer?.DocNum, result?.Transfer?.DocEntry);
                return (true, result?.Message ?? "Inventory transfer created successfully", result?.Transfer);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Inventory transfer creation failed. Status: {StatusCode}, Response: {ErrorContent}",
                response.StatusCode, errorContent);

            // Parse the error response using JsonDocument to handle all API error shapes
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(errorContent);
                var root = doc.RootElement;

                var message = root.TryGetProperty("message", out var msgProp) || root.TryGetProperty("Message", out msgProp)
                    ? msgProp.GetString() ?? "Failed to create inventory transfer"
                    : "Failed to create inventory transfer";

                // Extract errors - could be List<string> or List<complex object>
                var errorMessages = new List<string>();
                var errorsPropertyName = root.TryGetProperty("errors", out var errorsProp) ? "errors"
                    : root.TryGetProperty("Errors", out errorsProp) ? "Errors" : null;

                if (errorsPropertyName != null && errorsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var err in errorsProp.EnumerateArray())
                    {
                        if (err.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            errorMessages.Add(err.GetString()!);
                        }
                        else if (err.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            // StockValidationError or similar complex error object
                            var errMsg = err.TryGetProperty("message", out var m) || err.TryGetProperty("Message", out m)
                                ? m.GetString() : null;
                            if (!string.IsNullOrEmpty(errMsg))
                                errorMessages.Add(errMsg);
                            else
                                errorMessages.Add(err.ToString());
                        }
                    }
                }

                // Also extract suggestions if present
                if (root.TryGetProperty("suggestions", out var sugProp) || root.TryGetProperty("Suggestions", out sugProp))
                {
                    if (sugProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var sug in sugProp.EnumerateArray())
                        {
                            if (sug.ValueKind == System.Text.Json.JsonValueKind.String)
                                errorMessages.Add($"Suggestion: {sug.GetString()}");
                        }
                    }
                }

                var fullMessage = errorMessages.Count > 0
                    ? $"{message}\n{string.Join("\n", errorMessages)}"
                    : message;

                return (false, fullMessage, null);
            }
            catch
            {
                return (false, $"Failed to create inventory transfer (HTTP {(int)response.StatusCode}): {errorContent}", null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error creating inventory transfer");
            return (false, $"Network error: {httpEx.Message}", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating inventory transfer");
            return (false, $"Error: {ex.Message}", null);
        }
    }

    #endregion

    #region Transfer Request Operations

    public async Task<(bool Success, string Message, InventoryTransferRequestDto? TransferRequest)> CreateTransferRequestAsync(CreateTransferRequestDto request)
    {
        try
        {
            _logger.LogInformation("Creating transfer request from {FromWarehouse} to {ToWarehouse} with {LineCount} lines",
                request.FromWarehouse, request.ToWarehouse, request.Lines.Count);

            var response = await _httpClient.PostAsJsonAsync("api/inventorytransfer/request", request);

            _logger.LogInformation("Transfer request API response: {StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TransferRequestCreatedResponse>();
                _logger.LogInformation("Transfer request created successfully: DocNum={DocNum}, DocEntry={DocEntry}",
                    result?.TransferRequest?.DocNum, result?.TransferRequest?.DocEntry);
                return (true, result?.Message ?? "Transfer request created successfully", result?.TransferRequest);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Transfer request creation failed. Status: {StatusCode}, Response: {ErrorContent}",
                response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var errorMessage = errorResponse?.Message ?? "Failed to create transfer request";

                if (errorResponse?.Errors?.Any() == true)
                {
                    errorMessage = string.Join("; ", errorResponse.Errors);
                }

                return (false, errorMessage, null);
            }
            catch
            {
                return (false, $"Failed to create transfer request: {response.StatusCode} - {errorContent}", null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error creating transfer request");
            return (false, $"Network error: {httpEx.Message}", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating transfer request");
            return (false, $"Error: {ex.Message}", null);
        }
    }

    public async Task<TransferRequestListResponse?> GetTransferRequestsAsync(int page = 1, int pageSize = 20)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<TransferRequestListResponse>($"api/inventorytransfer/requests?page={page}&pageSize={pageSize}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfer requests");
            return null;
        }
    }

    public async Task<TransferRequestListResponse?> GetTransferRequestsByWarehouseAsync(string warehouseCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<TransferRequestListResponse>($"api/inventorytransfer/requests/{warehouseCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfer requests for warehouse {Warehouse}", warehouseCode);
            return null;
        }
    }

    public async Task<InventoryTransferRequestDto?> GetTransferRequestByDocEntryAsync(int docEntry)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InventoryTransferRequestDto>($"api/inventorytransfer/request/{docEntry}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfer request {DocEntry}", docEntry);
            return null;
        }
    }

    public async Task<(bool Success, string Message, InventoryTransferDto? Transfer)> ConvertTransferRequestToTransferAsync(int docEntry)
    {
        try
        {
            _logger.LogInformation("Converting transfer request {DocEntry} to inventory transfer", docEntry);

            var response = await _httpClient.PostAsync($"api/inventorytransfer/request/{docEntry}/convert", null);

            _logger.LogInformation("Convert transfer request API response: {StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TransferRequestConvertedResponse>();
                _logger.LogInformation("Transfer request converted successfully: TransferDocEntry={DocEntry}",
                    result?.Transfer?.DocEntry);
                return (true, result?.Message ?? "Transfer request converted successfully", result?.Transfer);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Transfer request conversion failed. Status: {StatusCode}, Response: {ErrorContent}",
                response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var errorMessage = errorResponse?.Message ?? "Failed to convert transfer request";

                if (errorResponse?.Errors?.Any() == true)
                {
                    errorMessage = string.Join("; ", errorResponse.Errors);
                }

                return (false, errorMessage, null);
            }
            catch
            {
                return (false, $"Failed to convert transfer request: {response.StatusCode} - {errorContent}", null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error converting transfer request");
            return (false, $"Network error: {httpEx.Message}", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error converting transfer request");
            return (false, $"Error: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message)> CloseTransferRequestAsync(int docEntry)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/inventorytransfer/request/{docEntry}/close", null);

            if (response.IsSuccessStatusCode)
            {
                return (true, $"Transfer request {docEntry} closed successfully");
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to close transfer request {DocEntry}: {StatusCode} - {Error}", docEntry, response.StatusCode, errorContent);
            return (false, $"Failed to close transfer request: {errorContent}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing transfer request {DocEntry}", docEntry);
            return (false, $"Error: {ex.Message}");
        }
    }

    #endregion
}
