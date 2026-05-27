using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Forms;
using ShopInventory.Web.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface ICrateTrackingService
{
    Task<List<CrateTransactionDto>?> GetTransactionsAsync(string? search = null, string? status = null, string? transactionType = null, CancellationToken cancellationToken = default);
    Task<List<CratePodSubmissionDto>?> GetPodsAsync(string? search = null, string? submissionRole = null, CancellationToken cancellationToken = default);
    Task<List<CrateGrvDto>?> GetGrvsAsync(string? search = null, string? status = null, CancellationToken cancellationToken = default);
    Task<BulkCratePodValidationResponse?> ValidateBulkCratePodsAsync(IEnumerable<int> invoiceDocNums, string? submissionRole = null, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message, CrateTransactionDto? Transaction)> CreateOpeningBalanceAsync(string shopCardCode, decimal quantity, DateTime effectiveDate, IBrowserFile? file, string? notes = null, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message, CrateTransactionDto? Transaction)> UpdateOpeningBalanceAsync(int crateTransactionId, string shopCardCode, decimal quantity, DateTime effectiveDate, IBrowserFile? file, string? notes = null, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message)> DeleteOpeningBalanceAsync(int crateTransactionId, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message, CratePodSubmissionDto? Submission)> UploadCratePodAsync(int crateTransactionId, decimal quantity, IBrowserFile file, string? submissionRole = null, string? notes = null, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message)> DeleteAttachmentAsync(int attachmentId, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message, CrateGrvDto? Grv)> CreateCrateGrvAsync(int crateTransactionId, string reason, IBrowserFile file, CancellationToken cancellationToken = default);
}

public class CrateTrackingService(HttpClient httpClient, ILogger<CrateTrackingService> logger, ILocalStorageService localStorage) : ICrateTrackingService
{
    private const long MaxUploadSize = 20 * 1024 * 1024;

    private async Task EnsureAuthenticationAsync()
    {
        try
        {
            var token = await localStorage.GetItemAsync<string>("authToken");
            var currentToken = httpClient.DefaultRequestHeaders.Authorization?.Parameter;

            if (string.IsNullOrWhiteSpace(token))
            {
                httpClient.DefaultRequestHeaders.Authorization = null;
                return;
            }

            if (!string.Equals(currentToken, token, StringComparison.Ordinal))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        catch
        {
        }
    }

    public async Task<List<CrateTransactionDto>?> GetTransactionsAsync(string? search = null, string? status = null, string? transactionType = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var url = BuildUrl("api/crates/transactions", ("search", search), ("status", status), ("transactionType", transactionType));
            return await httpClient.GetFromJsonAsync<List<CrateTransactionDto>>(url, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading crate transactions");
            return null;
        }
    }

    public async Task<List<CratePodSubmissionDto>?> GetPodsAsync(string? search = null, string? submissionRole = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var url = BuildUrl("api/crates/pods", ("search", search), ("submissionRole", submissionRole));
            return await httpClient.GetFromJsonAsync<List<CratePodSubmissionDto>>(url, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading crate PODs");
            return null;
        }
    }

    public async Task<List<CrateGrvDto>?> GetGrvsAsync(string? search = null, string? status = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var url = BuildUrl("api/crates/grvs", ("search", search), ("status", status));
            return await httpClient.GetFromJsonAsync<List<CrateGrvDto>>(url, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading crate GRVs");
            return null;
        }
    }

    public async Task<BulkCratePodValidationResponse?> ValidateBulkCratePodsAsync(
        IEnumerable<int> invoiceDocNums,
        string? submissionRole = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();

            var request = new BulkCratePodValidationRequest
            {
                InvoiceDocNums = invoiceDocNums
                    .Where(invoiceDocNum => invoiceDocNum > 0)
                    .Distinct()
                    .ToList(),
                SubmissionRole = string.IsNullOrWhiteSpace(submissionRole)
                    ? null
                    : submissionRole.Trim()
            };

            if (request.InvoiceDocNums.Count == 0)
            {
                return new BulkCratePodValidationResponse();
            }

            var response = await httpClient.PostAsJsonAsync("api/crates/pods/validate-bulk", request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<BulkCratePodValidationResponse>(cancellationToken);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Bulk crate POD validation failed: {StatusCode} {Payload}",
                (int)response.StatusCode,
                ApiErrorResponse.SanitizeForLog(payload));

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating bulk crate PODs");
            return null;
        }
    }

    public async Task<(bool Success, string Message, CrateTransactionDto? Transaction)> CreateOpeningBalanceAsync(
        string shopCardCode,
        decimal quantity,
        DateTime effectiveDate,
        IBrowserFile? file,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(shopCardCode), "shopCardCode");
            content.Add(new StringContent(quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)), "quantity");
            content.Add(new StringContent(effectiveDate.ToString("yyyy-MM-dd")), "effectiveDate");

            if (file is not null)
            {
                await AddFileContentAsync(content, file, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                content.Add(new StringContent(notes), "notes");
            }

            var response = await httpClient.PostAsync("api/crates/opening-balances", content, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Opening balance uploaded successfully.", System.Text.Json.JsonSerializer.Deserialize<CrateTransactionDto>(payload, JsonDefaults.Options));
            }

            return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, payload, "We couldn't upload this opening balance right now. Please try again."), null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating crate opening balance");
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, "We couldn't upload this opening balance right now. Please try again."), null);
        }
    }

    public async Task<(bool Success, string Message, CrateTransactionDto? Transaction)> UpdateOpeningBalanceAsync(
        int crateTransactionId,
        string shopCardCode,
        decimal quantity,
        DateTime effectiveDate,
        IBrowserFile? file,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(shopCardCode), "shopCardCode");
            content.Add(new StringContent(quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)), "quantity");
            content.Add(new StringContent(effectiveDate.ToString("yyyy-MM-dd")), "effectiveDate");

            if (file is not null)
            {
                await AddFileContentAsync(content, file, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                content.Add(new StringContent(notes), "notes");
            }

            var response = await httpClient.PutAsync($"api/crates/opening-balances/{crateTransactionId}", content, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Opening balance updated successfully.", System.Text.Json.JsonSerializer.Deserialize<CrateTransactionDto>(payload, JsonDefaults.Options));
            }

            return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, payload, "We couldn't update this opening balance right now. Please try again."), null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating crate opening balance {TransactionId}", crateTransactionId);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, "We couldn't update this opening balance right now. Please try again."), null);
        }
    }

    public async Task<(bool Success, string Message)> DeleteOpeningBalanceAsync(
        int crateTransactionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await httpClient.DeleteAsync($"api/crates/opening-balances/{crateTransactionId}", cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Opening balance deleted successfully.");
            }

            return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, payload, "We couldn't delete this opening balance right now. Please try again."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting crate opening balance {TransactionId}", crateTransactionId);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, "We couldn't delete this opening balance right now. Please try again."));
        }
    }

    public async Task<(bool Success, string Message, CratePodSubmissionDto? Submission)> UploadCratePodAsync(
        int crateTransactionId,
        decimal quantity,
        IBrowserFile file,
        string? submissionRole = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            using var content = await BuildFileContentAsync(file, cancellationToken);
            content.Add(new StringContent(quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)), "quantity");

            if (!string.IsNullOrWhiteSpace(submissionRole))
            {
                content.Add(new StringContent(submissionRole), "submissionRole");
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                content.Add(new StringContent(notes), "notes");
            }

            var response = await httpClient.PostAsync($"api/crates/transactions/{crateTransactionId}/pods", content, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Crate POD uploaded successfully.", System.Text.Json.JsonSerializer.Deserialize<CratePodSubmissionDto>(payload, JsonDefaults.Options));
            }

            return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, payload, "We couldn't upload this crate POD right now. Please try again."), null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading crate POD for transaction {TransactionId}", crateTransactionId);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, "We couldn't upload this crate POD right now. Please try again."), null);
        }
    }

    public async Task<(bool Success, string Message)> DeleteAttachmentAsync(
        int attachmentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            var response = await httpClient.DeleteAsync($"api/document/attachments/{attachmentId}", cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Attachment deleted successfully.");
            }

            return (
                false,
                ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    payload,
                    "We couldn't delete this attachment right now. Please try again."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting crate attachment {AttachmentId}", attachmentId);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, "We couldn't delete this attachment right now. Please try again."));
        }
    }

    public async Task<(bool Success, string Message, CrateGrvDto? Grv)> CreateCrateGrvAsync(
        int crateTransactionId,
        string reason,
        IBrowserFile file,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticationAsync();
            using var content = await BuildFileContentAsync(file, cancellationToken);
            content.Add(new StringContent(reason), "reason");

            var response = await httpClient.PostAsync($"api/crates/transactions/{crateTransactionId}/grvs", content, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Crate GRV created successfully.", System.Text.Json.JsonSerializer.Deserialize<CrateGrvDto>(payload, JsonDefaults.Options));
            }

            return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, payload, "We couldn't create this crate GRV right now. Please try again."), null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating crate GRV for transaction {TransactionId}", crateTransactionId);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, "We couldn't create this crate GRV right now. Please try again."), null);
        }
    }

    private static string BuildUrl(string basePath, params (string Key, string? Value)[] queryParts)
    {
        var parts = queryParts
            .Where(part => !string.IsNullOrWhiteSpace(part.Value))
            .Select(part => $"{part.Key}={Uri.EscapeDataString(part.Value!.Trim())}")
            .ToList();

        return parts.Count == 0 ? basePath : $"{basePath}?{string.Join("&", parts)}";
    }

    private static async Task<MultipartFormDataContent> BuildFileContentAsync(IBrowserFile file, CancellationToken cancellationToken)
    {
        var content = new MultipartFormDataContent();
        await AddFileContentAsync(content, file, cancellationToken);
        return content;
    }

    private static async Task AddFileContentAsync(MultipartFormDataContent content, IBrowserFile file, CancellationToken cancellationToken)
    {
        using var sourceStream = file.OpenReadStream(MaxUploadSize, cancellationToken);
        var memoryStream = new MemoryStream();
        await sourceStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        var streamContent = new StreamContent(memoryStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        content.Add(streamContent, "file", file.Name);
    }

    private static class JsonDefaults
    {
        internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}