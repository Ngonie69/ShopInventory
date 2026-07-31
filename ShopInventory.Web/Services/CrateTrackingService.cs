using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Forms;
using ShopInventory.Web.Models;
using System.Net;
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
    Task<(bool Success, string Message)> DeletePodAsync(int cratePodSubmissionId, CancellationToken cancellationToken = default);
    Task<(bool Success, string Message, CrateGrvDto? Grv)> CreateCrateGrvAsync(int crateTransactionId, string reason, IBrowserFile file, CancellationToken cancellationToken = default);
}

public class CrateTrackingService(
    HttpClient httpClient,
    ILogger<CrateTrackingService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider) : ICrateTrackingService
{
    private const long MaxUploadSize = 20 * 1024 * 1024;

    private async Task EnsureAuthenticationAsync()
    {
        try
        {
            // Goes through the auth state provider so an expired access token is renewed from the
            // refresh token instead of being sent to the API and coming back as a 401.
            var token = await authStateProvider.GetAccessTokenAsync()
                        ?? await localStorage.GetItemAsync<string>("authToken");
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

    /// <summary>
    /// Sends an authenticated request, renewing the access token and retrying once if the API
    /// rejects it. The request factory is invoked per attempt because a request message and its
    /// multipart content cannot be resent.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
    {
        await EnsureAuthenticationAsync();
        var response = await sendAsync();

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var rejectedToken = httpClient.DefaultRequestHeaders.Authorization?.Parameter;
        var refreshedToken = await authStateProvider.RefreshAccessTokenAsync(rejectedToken);
        if (string.IsNullOrWhiteSpace(refreshedToken) ||
            string.Equals(refreshedToken, rejectedToken, StringComparison.Ordinal))
        {
            return response;
        }

        logger.LogInformation("Retrying request after refreshing an expired access token");
        response.Dispose();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
        return await sendAsync();
    }

    public async Task<List<CrateTransactionDto>?> GetTransactionsAsync(string? search = null, string? status = null, string? transactionType = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = BuildUrl("api/crates/transactions", ("search", search), ("status", status), ("transactionType", transactionType));
            using var response = await SendAuthenticatedAsync(() => httpClient.GetAsync(url, cancellationToken));
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<CrateTransactionDto>>(cancellationToken);
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
            var url = BuildUrl("api/crates/pods", ("search", search), ("submissionRole", submissionRole));
            using var response = await SendAuthenticatedAsync(() => httpClient.GetAsync(url, cancellationToken));
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<CratePodSubmissionDto>>(cancellationToken);
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
            var url = BuildUrl("api/crates/grvs", ("search", search), ("status", status));
            using var response = await SendAuthenticatedAsync(() => httpClient.GetAsync(url, cancellationToken));
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<CrateGrvDto>>(cancellationToken);
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

            using var response = await SendAuthenticatedAsync(() => httpClient.PostAsJsonAsync("api/crates/pods/validate-bulk", request, cancellationToken));
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
            var bufferedFile = file is null ? (BufferedFile?)null : await ReadFileAsync(file, cancellationToken);

            using var response = await SendAuthenticatedAsync(async () =>
            {
                using var content = BuildOpeningBalanceContent(shopCardCode, quantity, effectiveDate, bufferedFile, notes);
                return await httpClient.PostAsync("api/crates/opening-balances", content, cancellationToken);
            });

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
            var bufferedFile = file is null ? (BufferedFile?)null : await ReadFileAsync(file, cancellationToken);

            using var response = await SendAuthenticatedAsync(async () =>
            {
                using var content = BuildOpeningBalanceContent(shopCardCode, quantity, effectiveDate, bufferedFile, notes);
                return await httpClient.PutAsync($"api/crates/opening-balances/{crateTransactionId}", content, cancellationToken);
            });

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
            using var response = await SendAuthenticatedAsync(
                () => httpClient.DeleteAsync($"api/crates/opening-balances/{crateTransactionId}", cancellationToken));
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
            var bufferedFile = await ReadFileAsync(file, cancellationToken);

            using var response = await SendAuthenticatedAsync(async () =>
            {
                using var content = BuildFileContent(bufferedFile);
                content.Add(new StringContent(quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)), "quantity");

                if (!string.IsNullOrWhiteSpace(submissionRole))
                {
                    content.Add(new StringContent(submissionRole), "submissionRole");
                }

                if (!string.IsNullOrWhiteSpace(notes))
                {
                    content.Add(new StringContent(notes), "notes");
                }

                return await httpClient.PostAsync($"api/crates/transactions/{crateTransactionId}/pods", content, cancellationToken);
            });

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

    public async Task<(bool Success, string Message)> DeletePodAsync(
        int cratePodSubmissionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAuthenticatedAsync(
                () => httpClient.DeleteAsync($"api/crates/pods/{cratePodSubmissionId}", cancellationToken));
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Crate POD deleted successfully.");
            }

            return (
                false,
                ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    payload,
                    "We couldn't delete this crate POD right now. Please try again."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting crate POD {CratePodSubmissionId}", cratePodSubmissionId);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, "We couldn't delete this crate POD right now. Please try again."));
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
            var bufferedFile = await ReadFileAsync(file, cancellationToken);

            using var response = await SendAuthenticatedAsync(async () =>
            {
                using var content = BuildFileContent(bufferedFile);
                content.Add(new StringContent(reason), "reason");

                return await httpClient.PostAsync($"api/crates/transactions/{crateTransactionId}/grvs", content, cancellationToken);
            });

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

    /// <summary>
    /// A browser file buffered into memory. Read once up front so the multipart body can be rebuilt
    /// if <see cref="SendAuthenticatedAsync"/> has to retry - the browser stream is not re-readable.
    /// </summary>
    private readonly record struct BufferedFile(byte[] Bytes, string FileName, string ContentType);

    private static async Task<BufferedFile> ReadFileAsync(IBrowserFile file, CancellationToken cancellationToken)
    {
        using var sourceStream = file.OpenReadStream(MaxUploadSize, cancellationToken);
        using var memoryStream = new MemoryStream();
        await sourceStream.CopyToAsync(memoryStream, cancellationToken);

        return new BufferedFile(
            memoryStream.ToArray(),
            file.Name,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
    }

    private static MultipartFormDataContent BuildOpeningBalanceContent(
        string shopCardCode,
        decimal quantity,
        DateTime effectiveDate,
        BufferedFile? file,
        string? notes)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(shopCardCode), "shopCardCode");
        content.Add(new StringContent(quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)), "quantity");
        content.Add(new StringContent(effectiveDate.ToString("yyyy-MM-dd")), "effectiveDate");

        if (file is { } bufferedFile)
        {
            AddFileContent(content, bufferedFile);
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            content.Add(new StringContent(notes), "notes");
        }

        return content;
    }

    private static MultipartFormDataContent BuildFileContent(BufferedFile file)
    {
        var content = new MultipartFormDataContent();
        AddFileContent(content, file);
        return content;
    }

    private static void AddFileContent(MultipartFormDataContent content, BufferedFile file)
    {
        var fileContent = new ByteArrayContent(file.Bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        content.Add(fileContent, "file", file.FileName);
    }

    private static class JsonDefaults
    {
        internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}