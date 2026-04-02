using ShopInventory.Web.Models;
using System.Net.Http.Json;
using System.Net.Http.Headers;

namespace ShopInventory.Web.Services;

public interface IPodService
{
    Task<PodAttachmentListResponse?> GetAllPodsAsync(int page = 1, int pageSize = 20, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, string? search = null);
    Task<PodAttachmentListResponse?> GetAllPodsForAccountsAsync(int page, int pageSize, List<string> cardCodes, DateTime? fromDate = null, DateTime? toDate = null);
    Task<DocumentAttachmentListResponse?> GetInvoicePodsAsync(int docEntry);
    Task<(bool Success, string Message, DocumentAttachmentDto? Attachment)> UploadPodAsync(int docEntry, Stream fileStream, string fileName, string contentType, string? description = null, string? uploadedByUsername = null);
    Task<byte[]?> DownloadPodAsync(int docEntry, int attachmentId);
    Task<bool> DeletePodAsync(int attachmentId);
    Task<PodUploadStatusReport?> GetPodUploadStatusAsync(DateTime fromDate, DateTime toDate);
    Task<PodDashboardModel?> GetPodDashboardAsync();
}

public class PodService : IPodService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PodService> _logger;

    public PodService(HttpClient httpClient, ILogger<PodService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PodAttachmentListResponse?> GetAllPodsAsync(int page = 1, int pageSize = 20, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, string? search = null)
    {
        try
        {
            var url = $"api/invoice/pods?page={page}&pageSize={pageSize}";
            if (!string.IsNullOrEmpty(cardCode))
                url += $"&cardCode={Uri.EscapeDataString(cardCode)}";
            if (fromDate.HasValue)
                url += $"&fromDate={fromDate.Value:yyyy-MM-dd}";
            if (toDate.HasValue)
                url += $"&toDate={toDate.Value:yyyy-MM-dd}";
            if (!string.IsNullOrEmpty(search))
                url += $"&search={Uri.EscapeDataString(search)}";

            return await _httpClient.GetFromJsonAsync<PodAttachmentListResponse>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching all PODs");
            return null;
        }
    }

    public async Task<PodAttachmentListResponse?> GetAllPodsForAccountsAsync(int page, int pageSize, List<string> cardCodes, DateTime? fromDate = null, DateTime? toDate = null)
    {
        if (cardCodes.Count == 0) return null;
        var joined = string.Join(",", cardCodes);
        return await GetAllPodsAsync(page, pageSize, joined, fromDate, toDate);
    }

    public async Task<DocumentAttachmentListResponse?> GetInvoicePodsAsync(int docEntry)
    {
        try
        {
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

    public async Task<(bool Success, string Message, DocumentAttachmentDto? Attachment)> UploadPodAsync(
        int docEntry, Stream fileStream, string fileName, string contentType, string? description = null, string? uploadedByUsername = null)
    {
        try
        {
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
            var response = await _httpClient.DeleteAsync($"api/document/attachments/{attachmentId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting POD {AttachmentId}", attachmentId);
            return false;
        }
    }

    public async Task<PodUploadStatusReport?> GetPodUploadStatusAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            var from = fromDate.ToString("yyyy-MM-dd");
            var to = toDate.ToString("yyyy-MM-dd");
            return await _httpClient.GetFromJsonAsync<PodUploadStatusReport>(
                $"api/invoice/pod-upload-status?fromDate={from}&toDate={to}");
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
            return await _httpClient.GetFromJsonAsync<PodDashboardModel>("api/invoice/pod-dashboard");
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
