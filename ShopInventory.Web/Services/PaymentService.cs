using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IPaymentService
{
    Task<IncomingPaymentListResponse?> GetPaymentsAsync(int page = 1, int pageSize = 20);
    Task<IncomingPaymentDto?> GetPaymentByDocEntryAsync(int docEntry);
    Task<IncomingPaymentDto?> GetPaymentByDocNumAsync(int docNum);
    Task<IncomingPaymentDateResponse?> GetPaymentsByDateAsync(DateTime date);
    Task<IncomingPaymentDateResponse?> GetPaymentsByDateRangeAsync(DateTime fromDate, DateTime toDate);
    Task<IncomingPaymentDateResponse?> GetPaymentsByCustomerAsync(string cardCode);
    Task<(bool Success, string Message, IncomingPaymentDto? Payment)> CreatePaymentAsync(CreateIncomingPaymentRequest request);
    Task<(bool Success, string Message)> UploadPaymentAttachmentAsync(int docEntry, Stream fileStream, string fileName, string contentType, string? description = null);
}

public class PaymentService : IPaymentService
{
    private readonly HttpClient _httpClient;
    private readonly IIncomingPaymentCacheService _cacheService;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        HttpClient httpClient,
        IIncomingPaymentCacheService cacheService,
        ILogger<PaymentService> logger)
    {
        _httpClient = httpClient;
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task<IncomingPaymentListResponse?> GetPaymentsAsync(int page = 1, int pageSize = 20)
    {
        try
        {
            // Use cache service for faster loading
            return await _cacheService.GetCachedPaymentsAsync(page, pageSize);
        }
        catch (TimeoutException)
        {
            // Re-throw timeout exceptions so the UI can handle them
            throw;
        }
        catch (HttpRequestException)
        {
            // Re-throw HTTP errors so the UI can show a meaningful message
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payments from cache, falling back to API");
            // Try direct API as last resort
            return await _httpClient.GetFromJsonAsync<IncomingPaymentListResponse>($"api/incomingpayment?page={page}&pageSize={pageSize}");
        }
    }

    public async Task<IncomingPaymentDto?> GetPaymentByDocEntryAsync(int docEntry)
    {
        try
        {
            // Use cache service
            return await _cacheService.GetCachedPaymentByDocEntryAsync(docEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payment from cache, falling back to API");
            try
            {
                return await _httpClient.GetFromJsonAsync<IncomingPaymentDto>($"api/incomingpayment/{docEntry}");
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<IncomingPaymentDto?> GetPaymentByDocNumAsync(int docNum)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<IncomingPaymentDto>($"api/incomingpayment/docnum/{docNum}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<IncomingPaymentDateResponse?> GetPaymentsByDateAsync(DateTime date)
    {
        try
        {
            // Always call API directly — the cache is a paginated snapshot and may not cover arbitrary dates
            var dateStr = date.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>(
                $"api/incomingpayment/daterange?fromDate={dateStr}&toDate={dateStr}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payments by date from API");
            return null;
        }
    }

    public async Task<IncomingPaymentDateResponse?> GetPaymentsByDateRangeAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            // Always call API directly — the cache is a paginated snapshot and may not cover arbitrary date ranges
            var from = fromDate.ToString("yyyy-MM-dd");
            var to = toDate.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>(
                $"api/incomingpayment/daterange?fromDate={from}&toDate={to}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payments by date range from API");
            return null;
        }
    }

    public async Task<IncomingPaymentDateResponse?> GetPaymentsByCustomerAsync(string cardCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<IncomingPaymentDateResponse>($"api/incomingpayment/customer/{cardCode}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Success, string Message, IncomingPaymentDto? Payment)> CreatePaymentAsync(CreateIncomingPaymentRequest request)
    {
        try
        {
            _logger.LogInformation("Sending incoming payment creation request for customer {CardCode}", request.CardCode);

            var response = await _httpClient.PostAsJsonAsync("api/incomingpayment", request);

            _logger.LogInformation("Incoming payment API response: {StatusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<IncomingPaymentCreatedResponse>();
                _logger.LogInformation("Incoming payment created successfully: DocNum={DocNum}, DocEntry={DocEntry}",
                    result?.Payment?.DocNum, result?.Payment?.DocEntry);
                return (true, result?.Message ?? "Incoming payment created successfully", result?.Payment);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Incoming payment creation failed. Status: {StatusCode}, Response: {ErrorContent}",
                response.StatusCode, errorContent);

            try
            {
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var errorMessage = errorResponse?.Message ?? "Failed to create incoming payment";
                if (errorResponse?.Errors?.Any() == true)
                {
                    errorMessage = string.Join("; ", errorResponse.Errors);
                }

                return (false, errorMessage, null);
            }
            catch
            {
                return (false, $"Failed to create incoming payment. Status: {response.StatusCode}", null);
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error creating incoming payment");
            throw;
        }
        catch (TaskCanceledException tcEx) when (tcEx.InnerException is TimeoutException)
        {
            _logger.LogError(tcEx, "Timeout creating incoming payment");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating incoming payment");
            throw;
        }
    }

    public async Task<(bool Success, string Message)> UploadPaymentAttachmentAsync(int docEntry, Stream fileStream, string fileName, string contentType, string? description = null)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(streamContent, "file", fileName);
            if (!string.IsNullOrWhiteSpace(description))
            {
                content.Add(new StringContent(description), "description");
            }

            var response = await _httpClient.PostAsync($"api/incomingpayment/{docEntry}/attachment", content);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Attachment uploaded successfully");
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to upload payment attachment. Status: {StatusCode}, Error: {Error}", response.StatusCode, error);
            return (false, "Failed to upload attachment");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading payment attachment for DocEntry {DocEntry}", docEntry);
            return (false, $"Error uploading attachment: {ex.Message}");
        }
    }
}
