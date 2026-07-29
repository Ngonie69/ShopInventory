using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IApprovalProcessService
{
    Task<List<ApprovalStageDefinitionModel>> GetStagesAsync();
    Task<(bool Success, string Message, ApprovalStageDefinitionModel? Value)> SaveStageAsync(ApprovalStageDefinitionModel stage);
    Task<(bool Success, string Message)> DeleteStageAsync(Guid id);
    Task<List<ApprovalTemplateDefinitionModel>> GetTemplatesAsync();
    Task<(bool Success, string Message, ApprovalTemplateDefinitionModel? Value)> SaveTemplateAsync(ApprovalTemplateDefinitionModel template);
    Task<(bool Success, string Message)> DeleteTemplateAsync(Guid id);
    Task<(bool Success, string Message, InventoryTransferDto? Transfer)> SubmitDecisionAsync(int docEntry, Guid stageId, string decision, string? remarks, bool generateDocument = false);
}

public sealed class ApprovalProcessService(HttpClient httpClient, ILogger<ApprovalProcessService> logger)
    : IApprovalProcessService
{
    public async Task<List<ApprovalStageDefinitionModel>> GetStagesAsync()
        => await httpClient.GetFromJsonAsync<List<ApprovalStageDefinitionModel>>("api/approval-process/stages") ?? [];

    public async Task<(bool, string, ApprovalStageDefinitionModel?)> SaveStageAsync(ApprovalStageDefinitionModel stage)
        => await SaveAsync<ApprovalStageDefinitionModel>("api/approval-process/stages", stage);

    public Task<(bool, string)> DeleteStageAsync(Guid id) => DeleteAsync($"api/approval-process/stages/{id}");

    public async Task<List<ApprovalTemplateDefinitionModel>> GetTemplatesAsync()
        => await httpClient.GetFromJsonAsync<List<ApprovalTemplateDefinitionModel>>("api/approval-process/templates") ?? [];

    public async Task<(bool, string, ApprovalTemplateDefinitionModel?)> SaveTemplateAsync(ApprovalTemplateDefinitionModel template)
        => await SaveAsync<ApprovalTemplateDefinitionModel>("api/approval-process/templates", template);

    public Task<(bool, string)> DeleteTemplateAsync(Guid id) => DeleteAsync($"api/approval-process/templates/{id}");

    public async Task<(bool Success, string Message, InventoryTransferDto? Transfer)> SubmitDecisionAsync(
        int docEntry, Guid stageId, string decision, string? remarks, bool generateDocument = false)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync($"api/approval-process/transfer-requests/{docEntry}/decision",
                new SubmitApprovalDecisionModel { StageId = stageId, Decision = decision, Remarks = remarks, GenerateDocument = generateDocument });
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, body, "The approval decision could not be saved."), null);
            if (string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase))
            {
                var result = await response.Content.ReadFromJsonAsync<TransferRequestConvertedResponse>();
                return (true, result?.Message ?? "Approval decision saved.", result?.Transfer);
            }
            var rejected = await response.Content.ReadFromJsonAsync<TransferRequestDecisionResponse>();
            return (true, rejected?.Message ?? "Rejection decision saved.", null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Approval decision failed for transfer request {DocEntry}", docEntry);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, "The approval decision could not be saved."), null);
        }
    }

    private async Task<(bool, string, T?)> SaveAsync<T>(string url, T value)
    {
        var response = await httpClient.PostAsJsonAsync(url, value);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, body, "The approval definition could not be saved."), default);
        return (true, "Saved successfully.", await response.Content.ReadFromJsonAsync<T>());
    }

    private async Task<(bool, string)> DeleteAsync(string url)
    {
        var response = await httpClient.DeleteAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        return response.IsSuccessStatusCode
            ? (true, "Deleted successfully.")
            : (false, ApiErrorResponse.GetFriendlyMessage(response.StatusCode, body, "The approval definition could not be deleted."));
    }
}
