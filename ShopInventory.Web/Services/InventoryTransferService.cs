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
    /// <summary>
    /// Submits an inventory transfer. It is held for approval, so <c>PendingTransfer</c> is
    /// returned and <c>Transfer</c> stays null until the approval process completes.
    /// </summary>
    Task<(bool Success, string Message, InventoryTransferDto? Transfer, PendingInventoryTransferDto? PendingTransfer)> CreateInventoryTransferAsync(CreateInventoryTransferDto request);

    // Direct transfers held for approval
    Task<PendingInventoryTransferListResponse?> GetPendingTransfersAsync(string? status = null, string? warehouseCode = null, bool mineOnly = false, int page = 1, int pageSize = 20);
    Task<PendingInventoryTransferDto?> GetPendingTransferAsync(Guid id);
    Task<(bool Success, string Message, InventoryTransferDto? Transfer)> DecidePendingTransferAsync(Guid id, string decision, Guid? stageId = null, string? remarks = null);
    Task<(bool Success, string Message, InventoryTransferDto? Transfer)> RetryPendingTransferPostAsync(Guid id);
    Task<(bool Success, string Message)> CancelPendingTransferAsync(Guid id);

    /// <summary>
    /// Changes an open transfer request's lines. Callers assigned the source warehouse update
    /// SAP directly; for anyone else the change comes back held for approval.
    /// </summary>
    Task<(bool Success, string Message, TransferRequestEditResponse? Result)> EditTransferRequestAsync(
        int docEntry, EditTransferRequestRequest request);

    Task<PendingTransferRequestEditListResponse?> GetPendingRequestEditsAsync(
        string? status = null, int? requestDocEntry = null, int pageSize = 50);

    Task<(bool Success, string Message)> DecidePendingRequestEditAsync(
        Guid id, string decision, Guid? stageId = null, string? remarks = null);

    Task<(bool Success, string Message)> CancelPendingRequestEditAsync(Guid id);

    // Transfer Request operations
    Task<(bool Success, string Message, InventoryTransferRequestDto? TransferRequest)> CreateTransferRequestAsync(CreateTransferRequestDto request);
    Task<TransferRequestListResponse?> GetTransferRequestsAsync(int page = 1, int pageSize = 20);
    Task<TransferRequestListResponse?> GetAllTransferRequestsAsync(int pageSize = 100);
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

    public async Task<(bool Success, string Message, InventoryTransferDto? Transfer, PendingInventoryTransferDto? PendingTransfer)> CreateInventoryTransferAsync(CreateInventoryTransferDto request)
    {
        try
        {
            _logger.LogInformation("Submitting inventory transfer from {FromWarehouse} to {ToWarehouse} with {LineCount} lines",
                request.FromWarehouse, request.ToWarehouse, request.Lines.Count);

            var clientRequestId = EnsureClientRequestId(request);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/inventorytransfer")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.Add("Idempotency-Key", clientRequestId);

            var response = await _httpClient.SendAsync(httpRequest);

            _logger.LogInformation("Inventory transfer API response: {StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<InventoryTransferCreatedResponse>();
                if (result?.RequiresApproval == true)
                {
                    _logger.LogInformation("Inventory transfer submitted for approval: PendingId={PendingId}",
                        result.PendingTransfer?.Id);
                }
                else
                {
                    _logger.LogInformation("Inventory transfer created successfully: DocNum={DocNum}, DocEntry={DocEntry}",
                        result?.Transfer?.DocNum, result?.Transfer?.DocEntry);
                }

                return (true,
                    result?.Message ?? "Inventory transfer submitted successfully",
                    result?.Transfer,
                    result?.PendingTransfer);
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

                return (false, fullMessage, null, null);
            }
            catch
            {
                return (false, ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    errorContent,
                    "We couldn't submit this inventory transfer right now. Please try again."), null, null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error submitting inventory transfer");
            return (false, ApiErrorResponse.GetFriendlyMessage(
                httpEx,
                "We couldn't submit this inventory transfer right now. Please try again."), null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error submitting inventory transfer");
            return (false, ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't submit this inventory transfer right now. Please try again."), null, null);
        }
    }

    private static string EnsureClientRequestId(CreateInventoryTransferDto request)
    {
        if (!string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            request.ClientRequestId = request.ClientRequestId.Trim();
            return request.ClientRequestId;
        }

        request.ClientRequestId = Guid.NewGuid().ToString("N");
        return request.ClientRequestId;
    }

    #endregion

    #region Pending (approval-held) Transfer Operations

    public async Task<PendingInventoryTransferListResponse?> GetPendingTransfersAsync(
        string? status = null, string? warehouseCode = null, bool mineOnly = false, int page = 1, int pageSize = 20)
    {
        try
        {
            var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
            if (!string.IsNullOrWhiteSpace(status))
                query.Add($"status={Uri.EscapeDataString(status)}");
            if (!string.IsNullOrWhiteSpace(warehouseCode))
                query.Add($"warehouseCode={Uri.EscapeDataString(warehouseCode)}");
            if (mineOnly)
                query.Add("mineOnly=true");

            return await _httpClient.GetFromJsonAsync<PendingInventoryTransferListResponse>(
                $"api/inventorytransfer/pending?{string.Join("&", query)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting inventory transfers awaiting approval");
            return null;
        }
    }

    public async Task<PendingInventoryTransferDto?> GetPendingTransferAsync(Guid id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<PendingInventoryTransferDto>($"api/inventorytransfer/pending/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pending inventory transfer {PendingId}", id);
            return null;
        }
    }

    public Task<(bool Success, string Message, InventoryTransferDto? Transfer)> DecidePendingTransferAsync(
        Guid id, string decision, Guid? stageId = null, string? remarks = null)
        => SendPendingTransferActionAsync(
            $"api/inventorytransfer/pending/{id}/decision",
            JsonContent.Create(new SubmitPendingTransferDecisionRequest { Decision = decision, StageId = stageId, Remarks = remarks }),
            "We couldn't record that decision right now. Please try again.");

    public Task<(bool Success, string Message, InventoryTransferDto? Transfer)> RetryPendingTransferPostAsync(Guid id)
        => SendPendingTransferActionAsync(
            $"api/inventorytransfer/pending/{id}/post",
            null,
            "We couldn't post this transfer right now. Please try again.");

    public async Task<(bool Success, string Message)> CancelPendingTransferAsync(Guid id)
    {
        var (success, message, _) = await SendPendingTransferActionAsync(
            $"api/inventorytransfer/pending/{id}/cancel",
            null,
            "We couldn't withdraw this transfer right now. Please try again.");
        return (success, message);
    }

    public async Task<(bool Success, string Message, TransferRequestEditResponse? Result)> EditTransferRequestAsync(
        int docEntry, EditTransferRequestRequest request)
    {
        try
        {
            var response = await _httpClient.PatchAsJsonAsync($"api/inventorytransfer/request/{docEntry}", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TransferRequestEditResponse>();
                return (true, result?.Message ?? "Transfer request updated.", result);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Editing transfer request {DocEntry} failed. Status: {StatusCode}, Response: {Error}",
                docEntry, response.StatusCode, errorContent);
            return (false, ExtractErrorMessage(errorContent)
                ?? ApiErrorResponse.GetFriendlyMessage(response.StatusCode, errorContent,
                    "We couldn't save that change right now. Please try again."), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error editing transfer request {DocEntry}", docEntry);
            return (false, "We couldn't save that change right now. Please try again.", null);
        }
    }

    public async Task<PendingTransferRequestEditListResponse?> GetPendingRequestEditsAsync(
        string? status = null, int? requestDocEntry = null, int pageSize = 50)
    {
        try
        {
            var query = new List<string> { $"pageSize={pageSize}" };
            if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
            if (requestDocEntry.HasValue) query.Add($"requestDocEntry={requestDocEntry.Value}");

            return await _httpClient.GetFromJsonAsync<PendingTransferRequestEditListResponse>(
                $"api/inventorytransfer/request-edits?{string.Join("&", query)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting held transfer request changes");
            return null;
        }
    }

    public Task<(bool Success, string Message)> DecidePendingRequestEditAsync(
        Guid id, string decision, Guid? stageId = null, string? remarks = null)
        => SendRequestEditActionAsync(
            $"api/inventorytransfer/request-edits/{id}/decision",
            JsonContent.Create(new SubmitPendingTransferDecisionRequest { Decision = decision, StageId = stageId, Remarks = remarks }),
            "We couldn't record that decision right now. Please try again.");

    public Task<(bool Success, string Message)> CancelPendingRequestEditAsync(Guid id)
        => SendRequestEditActionAsync(
            $"api/inventorytransfer/request-edits/{id}/cancel",
            null,
            "We couldn't withdraw that change right now. Please try again.");

    private async Task<(bool Success, string Message)> SendRequestEditActionAsync(
        string url,
        HttpContent? content,
        string fallbackMessage)
    {
        try
        {
            var response = await _httpClient.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<PendingTransferRequestEditDecisionResponse>();
                return (true, result?.Message ?? "Done.");
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Transfer request change action {Url} failed. Status: {StatusCode}, Response: {Error}",
                url, response.StatusCode, errorContent);
            return (false, ExtractErrorMessage(errorContent)
                ?? ApiErrorResponse.GetFriendlyMessage(response.StatusCode, errorContent, fallbackMessage));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling {Url}", url);
            return (false, fallbackMessage);
        }
    }

    private static string? ExtractErrorMessage(string errorContent)
    {
        try
        {
            var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var message = errorResponse?.Errors?.Any() == true
                ? string.Join("; ", errorResponse.Errors)
                : errorResponse?.Message;
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Success, string Message, InventoryTransferDto? Transfer)> SendPendingTransferActionAsync(
        string url,
        HttpContent? content,
        string fallbackMessage)
    {
        try
        {
            var response = await _httpClient.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<PendingInventoryTransferDecisionResponse>();
                return (true, result?.Message ?? "Done.", result?.Transfer);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Pending transfer action {Url} failed. Status: {StatusCode}, Response: {Error}",
                url, response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var errorMessage = errorResponse?.Errors?.Any() == true
                    ? string.Join("; ", errorResponse.Errors)
                    : errorResponse?.Message;
                if (!string.IsNullOrWhiteSpace(errorMessage))
                    return (false, errorMessage, null);
            }
            catch { }

            return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, errorContent, fallbackMessage), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling pending transfer action {Url}", url);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallbackMessage), null);
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
                return (false, ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    errorContent,
                    "We couldn't create this transfer request right now. Please try again."), null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error creating transfer request");
            return (false, ApiErrorResponse.GetFriendlyMessage(
                httpEx,
                "We couldn't create this transfer request right now. Please try again."), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating transfer request");
            return (false, ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't create this transfer request right now. Please try again."), null);
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

    public async Task<TransferRequestListResponse?> GetAllTransferRequestsAsync(int pageSize = 100)
    {
        var effectivePageSize = Math.Clamp(pageSize, 1, 100);
        var allRequests = new List<InventoryTransferRequestDto>();
        string? warehouse = null;
        var page = 1;

        while (true)
        {
            var response = await GetTransferRequestsAsync(page, effectivePageSize);
            if (response == null)
                return null;

            warehouse ??= response.Warehouse;

            var pageRequests = response.TransferRequests ?? new List<InventoryTransferRequestDto>();
            allRequests.AddRange(pageRequests);

            if (!response.HasMore || pageRequests.Count == 0)
                break;

            page++;
        }

        return new TransferRequestListResponse
        {
            Warehouse = warehouse,
            Page = 1,
            PageSize = effectivePageSize,
            Count = allRequests.Count,
            HasMore = false,
            TransferRequests = allRequests
        };
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
                return (false, ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    errorContent,
                    "We couldn't convert this transfer request right now. Please try again."), null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error converting transfer request");
            return (false, ApiErrorResponse.GetFriendlyMessage(
                httpEx,
                "We couldn't convert this transfer request right now. Please try again."), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error converting transfer request");
            return (false, ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't convert this transfer request right now. Please try again."), null);
        }
    }

    public async Task<(bool Success, string Message)> CloseTransferRequestAsync(int docEntry)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/inventorytransfer/request/{docEntry}/close", null);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TransferRequestDecisionResponse>();
                return (true, result?.Message ?? $"Transfer request {docEntry} rejected successfully");
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to reject transfer request {DocEntry}: {StatusCode} - {Error}", docEntry, response.StatusCode, errorContent);
            return (false, ApiErrorResponse.GetFriendlyMessage(
                response.StatusCode,
                errorContent,
                "We couldn't reject this transfer request right now. Please try again."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting transfer request {DocEntry}", docEntry);
            return (false, ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't reject this transfer request right now. Please try again."));
        }
    }

    #endregion
}
