using System.Net.Http.Json;
using ShopInventory.Web.Common.Http;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The thin transport for <c>/api/sap-transfer-approvals</c>. URLs are copied from the controller's route
/// attributes, not typed from memory: a string here that matches no route reports as a clean "nothing
/// found" forever.
/// </summary>
public interface ISapTransferApprovalService
{
    Task<(bool Success, string Message, SapTransferApprovalListResponseDto? Value)> GetApprovalsAsync(
        int page, int pageSize, int? beforeDocEntry = null, SapTransferQueueFilter? filter = null);

    Task<(bool Success, string Message, SapTransferApprovalDetailDto? Value)> GetApprovalAsync(int draftEntry);

    Task<(bool Success, string Message, AddApprovedSapTransferResultDto? Value)> AddAsync(int draftEntry, string clientRequestId);
}

public sealed class SapTransferApprovalService(HttpClient httpClient, ILogger<SapTransferApprovalService> logger)
    : ISapTransferApprovalService
{
    public async Task<(bool Success, string Message, SapTransferApprovalListResponseDto? Value)> GetApprovalsAsync(
        int page, int pageSize, int? beforeDocEntry = null, SapTransferQueueFilter? filter = null)
    {
        const string fallback = "The transfers SAP is holding could not be read.";
        try
        {
            var query = $"page={page}&pageSize={pageSize}";
            if (beforeDocEntry is int cursor)
            {
                query += $"&beforeDocEntry={cursor}";
            }

            if (filter is not null)
            {
                query += filter.ToQueryString();
            }

            return await ReadAsync<SapTransferApprovalListResponseDto>($"api/sap-transfer-approvals?{query}", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the transfers SAP is holding");
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, SapTransferApprovalDetailDto? Value)> GetApprovalAsync(int draftEntry)
    {
        var fallback = $"Transfer draft {draftEntry} could not be read from SAP.";
        try
        {
            return await ReadAsync<SapTransferApprovalDetailDto>($"api/sap-transfer-approvals/{draftEntry}", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching SAP transfer draft {DraftEntry}", draftEntry);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, AddApprovedSapTransferResultDto? Value)> AddAsync(int draftEntry, string clientRequestId)
    {
        const string fallback = "The transfer could not be added in SAP.";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/sap-transfer-approvals/{draftEntry}/add")
            {
                Content = JsonContent.Create(new AddApprovedSapTransferRequestDto { ClientRequestId = clientRequestId })
            };
            request.Headers.Add("Idempotency-Key", clientRequestId);

            using var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                // A 403 here is the warehouse rule saying why, not a missing permission.
                return (false, ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode, body, fallback, forbiddenMessage: ProblemDetailReader.ReadMessage(body)), null);
            }

            var result = await response.Content.ReadFromJsonAsync<AddApprovedSapTransferResultDto>();
            return (true, result?.Message ?? "Transfer added.", result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding SAP transfer draft {DraftEntry}", draftEntry);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    private async Task<(bool Success, string Message, T? Value)> ReadAsync<T>(string path, string fallback)
        where T : class
    {
        using var response = await httpClient.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            logger.LogWarning("GET {Path} answered {StatusCode}", path, (int)response.StatusCode);
            return (false, ApiErrorResponse.GetFriendlyMessage(
                response.StatusCode, body, fallback, forbiddenMessage: ProblemDetailReader.ReadMessage(body)), null);
        }

        var value = await response.Content.ReadFromJsonAsync<T>();
        return value is null ? (false, fallback, null) : (true, string.Empty, value);
    }
}
