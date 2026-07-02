using Blazored.LocalStorage;
using ShopInventory.Web.Models;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace ShopInventory.Web.Services;

public interface IPodService
{
    Task<PodAttachmentListResponse?> GetAllPodsAsync(int page = 1, int pageSize = 20, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, string? search = null, string? uploadedByUsername = null, string? uploadedFromLocation = null, CancellationToken cancellationToken = default);
    Task<PodAttachmentListResponse?> GetAllPodsForAccountsAsync(int page, int pageSize, List<string> cardCodes, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<DocumentAttachmentListResponse?> GetInvoicePodsAsync(int docEntry);
    Task<BulkPodValidationResponse?> ValidateBulkPodsAsync(IEnumerable<int> docNums, CancellationToken cancellationToken = default);
    Task<BulkPodValidationResponse?> ValidateBulkSalesOrderPodsAsync(IEnumerable<int> salesOrderDocNums, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message, DocumentAttachmentDto? Attachment)> UploadPodAsync(int docEntry, Stream fileStream, string fileName, string contentType, string? description = null, string? uploadedByUsername = null);
    Task<byte[]?> DownloadPodAsync(int docEntry, int attachmentId);
    Task<bool> DeletePodAsync(int attachmentId);
    Task<PodUploadStatusReport?> GetPodUploadStatusAsync(DateTime fromDate, DateTime toDate, bool includeCreditNoteActivity = true);
    Task<PodDashboardModel?> GetPodDashboardAsync();
}

public class PodService : IPodService
{
    private const int AuthenticationRetryCount = 15;
    private static readonly TimeSpan AuthenticationRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly HttpClient _httpClient;
    private readonly ILogger<PodService> _logger;
    private readonly ILocalStorageService _localStorage;

    public PodService(HttpClient httpClient, ILogger<PodService> logger, ILocalStorageService localStorage)
    {
        _httpClient = httpClient;
        _logger = logger;
        _localStorage = localStorage;
    }

    private async Task EnsureAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < AuthenticationRetryCount; attempt++)
        {
            try
            {
                var token = await _localStorage.GetItemAsync<string>("authToken");
                var currentToken = _httpClient.DefaultRequestHeaders.Authorization?.Parameter;

                if (string.IsNullOrWhiteSpace(token))
                {
                    if (attempt == AuthenticationRetryCount - 1)
                    {
                        _httpClient.DefaultRequestHeaders.Authorization = null;
                        _logger.LogWarning("POD auth token was unavailable after {AttemptCount} attempts", AuthenticationRetryCount);
                        return;
                    }
                }
                else
                {
                    if (!string.Equals(currentToken, token, StringComparison.Ordinal))
                    {
                        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }

                    return;
                }
            }
            catch (Exception ex) when (attempt < AuthenticationRetryCount - 1)
            {
                _logger.LogDebug(ex, "POD auth token is not available yet on attempt {Attempt}", attempt + 1);
            }

            if (attempt < AuthenticationRetryCount - 1)
            {
                await Task.Delay(AuthenticationRetryDelay, cancellationToken);
            }
        }
    }

    private async Task<T?> GetAuthenticatedJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticationAsync(cancellationToken);
        using var response = await SendAuthenticatedGetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "POD API GET {Url} failed with status {StatusCode}. Response: {ResponseBody}",
                url,
                (int)response.StatusCode,
                errorBody);
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedGetAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        _httpClient.DefaultRequestHeaders.Authorization = null;
        await EnsureAuthenticationAsync(cancellationToken);
        return await _httpClient.GetAsync(url, cancellationToken);
    }

    public async Task<PodAttachmentListResponse?> GetAllPodsAsync(int page = 1, int pageSize = 20, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, string? search = null, string? uploadedByUsername = null, string? uploadedFromLocation = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync(cancellationToken);
            var url = $"api/invoice/pods?page={page}&pageSize={pageSize}";
            if (!string.IsNullOrEmpty(cardCode))
                url += $"&cardCode={Uri.EscapeDataString(cardCode)}";
            if (fromDate.HasValue)
                url += $"&fromDate={fromDate.Value:yyyy-MM-dd}";
            if (toDate.HasValue)
                url += $"&toDate={toDate.Value:yyyy-MM-dd}";
            if (!string.IsNullOrEmpty(search))
                url += $"&search={Uri.EscapeDataString(search)}";
            if (!string.IsNullOrWhiteSpace(uploadedByUsername))
                url += $"&uploadedByUsername={Uri.EscapeDataString(uploadedByUsername)}";
            if (!string.IsNullOrWhiteSpace(uploadedFromLocation))
                url += $"&uploadedFromLocation={Uri.EscapeDataString(uploadedFromLocation)}";

            return await GetAuthenticatedJsonAsync<PodAttachmentListResponse>(url, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching all PODs");
            return null;
        }
    }

    public async Task<PodAttachmentListResponse?> GetAllPodsForAccountsAsync(int page, int pageSize, List<string> cardCodes, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
    {
        if (cardCodes.Count == 0) return null;
        var joined = string.Join(",", cardCodes);
        return await GetAllPodsAsync(page, pageSize, joined, fromDate, toDate, cancellationToken: cancellationToken);
    }

    public async Task<DocumentAttachmentListResponse?> GetInvoicePodsAsync(int docEntry)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.GetFromJsonAsync<DocumentAttachmentListResponse>(
                $"api/invoice/{docEntry}/attachments");

            if (response?.Attachments != null)
            {
                response.Attachments = response.Attachments
                    .Where(a => IsPodAttachment(a))
                    .ToList();
                response.TotalCount = response.Attachments.Count;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching PODs for invoice {DocEntry}", docEntry);
            return null;
        }
    }

    public async Task<BulkPodValidationResponse?> ValidateBulkPodsAsync(IEnumerable<int> docNums, CancellationToken cancellationToken = default)
        => await ValidateBulkPodsCoreAsync(docNums, null, cancellationToken);

    public async Task<BulkPodValidationResponse?> ValidateBulkSalesOrderPodsAsync(IEnumerable<int> salesOrderDocNums, CancellationToken cancellationToken = default)
        => await ValidateBulkPodsCoreAsync(null, salesOrderDocNums, cancellationToken);

    private async Task<BulkPodValidationResponse?> ValidateBulkPodsCoreAsync(
        IEnumerable<int>? docNums,
        IEnumerable<int>? salesOrderDocNums,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureAuthenticationAsync(cancellationToken);
            var request = new BulkPodValidationRequest
            {
                DocNums = (docNums ?? Enumerable.Empty<int>()).Where(docNum => docNum > 0).Distinct().ToList(),
                SalesOrderDocNums = (salesOrderDocNums ?? Enumerable.Empty<int>()).Where(docNum => docNum > 0).Distinct().ToList()
            };

            if (request.DocNums.Count == 0 && request.SalesOrderDocNums.Count == 0)
                return new BulkPodValidationResponse();

            var response = await _httpClient.PostAsJsonAsync("api/invoice/pods/validate-bulk", request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<BulkPodValidationResponse>(cancellationToken);

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Bulk POD validation failed: {StatusCode} {Error}", (int)response.StatusCode, error);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating bulk POD status");
            return null;
        }
    }

    public async Task<(bool Success, string Message, DocumentAttachmentDto? Attachment)> UploadPodAsync(
        int docEntry, Stream fileStream, string fileName, string contentType, string? description = null, string? uploadedByUsername = null)
    {
        try
        {
            await EnsureAuthenticationAsync();
            // Buffer the Blazor BrowserFileStream into a MemoryStream.
            // BrowserFileStream is non-seekable (reads via SignalR) and causes
            // MultipartFormDataContent to fail computing content-length.
            using var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(memoryStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(streamContent, "file", fileName);

            if (!string.IsNullOrWhiteSpace(description))
            {
                content.Add(new StringContent(description), "description");
            }

            if (!string.IsNullOrWhiteSpace(uploadedByUsername))
            {
                content.Add(new StringContent(uploadedByUsername), "uploadedByUsername");
            }

            var response = await _httpClient.PostAsync($"api/invoice/{docEntry}/pod", content);

            if (response.IsSuccessStatusCode)
            {
                var attachment = await response.Content.ReadFromJsonAsync<DocumentAttachmentDto>();
                return (true, "POD uploaded successfully", attachment);
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("POD upload failed for invoice {DocEntry}: {StatusCode} {Error}", docEntry, (int)response.StatusCode, error);
            return (false, $"Upload failed ({(int)response.StatusCode}). Please try again.", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading POD for invoice {DocEntry}", docEntry);
            return (false, "An error occurred while uploading the POD.", null);
        }
    }

    public async Task<byte[]?> DownloadPodAsync(int docEntry, int attachmentId)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.GetAsync(
                $"api/invoice/{docEntry}/attachments/{attachmentId}/download");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading POD {AttachmentId} for invoice {DocEntry}", attachmentId, docEntry);
            return null;
        }
    }

    public async Task<bool> DeletePodAsync(int attachmentId)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await _httpClient.DeleteAsync($"api/document/attachments/{attachmentId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting POD {AttachmentId}", attachmentId);
            return false;
        }
    }

    public async Task<PodUploadStatusReport?> GetPodUploadStatusAsync(
        DateTime fromDate,
        DateTime toDate,
        bool includeCreditNoteActivity = true)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var from = fromDate.ToString("yyyy-MM-dd");
            var to = toDate.ToString("yyyy-MM-dd");
            var includeCreditNoteActivityText = includeCreditNoteActivity ? "true" : "false";
            return await GetAuthenticatedJsonAsync<PodUploadStatusReport>(
                $"api/invoice/pod-upload-status?fromDate={from}&toDate={to}&includeCreditNoteActivity={includeCreditNoteActivityText}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching POD upload status report");
            return null;
        }
    }

    public async Task<PodDashboardModel?> GetPodDashboardAsync()
    {
        try
        {
            await EnsureAuthenticationAsync();
            return await GetAuthenticatedJsonAsync<PodDashboardModel>("api/invoice/pod-dashboard");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching POD dashboard");
            return null;
        }
    }

    private static bool IsPodAttachment(DocumentAttachmentDto attachment)
    {
        var label = $"{attachment.FileName} {attachment.Description}";
        return label.Contains("pod", StringComparison.OrdinalIgnoreCase)
            || label.Contains("proof of delivery", StringComparison.OrdinalIgnoreCase);
    }
}
