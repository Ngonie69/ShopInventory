using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The thin transport for <c>/api/credit-note-approvals</c>. URLs are copied from the controller's
/// route attributes, not typed from memory: a string here that matches no route reports as a clean
/// "nothing found" forever.
/// </summary>
public interface ICreditNoteApprovalService
{
    Task<CreditNoteApprovalListResponseDto?> GetApprovalsAsync(string? status, int page, int pageSize, int? beforeCode = null);
    Task<CreditNoteApprovalDetailDto?> GetApprovalAsync(int code);

    Task<(bool Success, string Message, CreditNoteApprovalDecisionResultDto? Value)> DecideAsync(
        int code, string decision, string? remarks, string clientRequestId);

    Task<(bool Success, string Message, AddApprovedCreditNoteResultDto? Value)> AddAsync(int code, string clientRequestId);
}

public sealed class CreditNoteApprovalService(HttpClient httpClient, ILogger<CreditNoteApprovalService> logger)
    : ICreditNoteApprovalService
{
    public async Task<CreditNoteApprovalListResponseDto?> GetApprovalsAsync(
        string? status, int page, int pageSize, int? beforeCode = null)
    {
        try
        {
            var query = $"page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(status))
            {
                query += $"&status={Uri.EscapeDataString(status)}";
            }

            if (beforeCode is int cursor)
            {
                query += $"&beforeCode={cursor}";
            }

            return await httpClient.GetFromJsonAsync<CreditNoteApprovalListResponseDto>($"api/credit-note-approvals?{query}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching SAP credit note approval requests");
            return null;
        }
    }

    public async Task<CreditNoteApprovalDetailDto?> GetApprovalAsync(int code)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<CreditNoteApprovalDetailDto>($"api/credit-note-approvals/{code}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching SAP credit note approval request {Code}", code);
            return null;
        }
    }

    public async Task<(bool Success, string Message, CreditNoteApprovalDecisionResultDto? Value)> DecideAsync(
        int code, string decision, string? remarks, string clientRequestId)
    {
        const string fallback = "The decision could not be recorded in SAP.";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/credit-note-approvals/{code}/decision")
            {
                Content = JsonContent.Create(new CreditNoteApprovalDecisionRequestDto
                {
                    Decision = decision,
                    Remarks = remarks,
                    ClientRequestId = clientRequestId
                })
            };
            request.Headers.Add("Idempotency-Key", clientRequestId);

            using var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, body, fallback), null);
            }

            var result = await response.Content.ReadFromJsonAsync<CreditNoteApprovalDecisionResultDto>();
            return (true, result?.Message ?? "Decision recorded.", result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error recording a decision on SAP credit note approval request {Code}", code);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, AddApprovedCreditNoteResultDto? Value)> AddAsync(int code, string clientRequestId)
    {
        const string fallback = "The credit note could not be added in SAP.";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/credit-note-approvals/{code}/add")
            {
                Content = JsonContent.Create(new AddApprovedCreditNoteRequestDto { ClientRequestId = clientRequestId })
            };
            request.Headers.Add("Idempotency-Key", clientRequestId);

            using var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, body, fallback), null);
            }

            var result = await response.Content.ReadFromJsonAsync<AddApprovedCreditNoteResultDto>();
            return (true, result?.Message ?? "Credit note added.", result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding the credit note for SAP approval request {Code}", code);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }
}
