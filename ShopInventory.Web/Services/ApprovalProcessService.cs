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

    /// <summary>
    /// Who a direct transfer with these warehouses would wait on, or null when the API could not say.
    /// Advisory only: the submit is what actually routes the transfer.
    /// </summary>
    Task<ApprovalRoutePreviewModel?> PreviewTransferRouteAsync(
        IReadOnlyCollection<string> fromWarehouses, IReadOnlyCollection<string> toWarehouses, CancellationToken cancellationToken = default);
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

    public async Task<ApprovalRoutePreviewModel?> PreviewTransferRouteAsync(
        IReadOnlyCollection<string> fromWarehouses,
        IReadOnlyCollection<string> toWarehouses,
        CancellationToken cancellationToken = default)
    {
        if (fromWarehouses.Count == 0 || toWarehouses.Count == 0)
            return null;

        var query = string.Join("&",
            fromWarehouses.Select(code => $"from={Uri.EscapeDataString(code)}")
                .Concat(toWarehouses.Select(code => $"to={Uri.EscapeDataString(code)}")));
        try
        {
            return await httpClient.GetFromJsonAsync<ApprovalRoutePreviewModel>(
                $"api/approval-process/transfers/route-preview?{query}", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // The form still submits without a preview; the person just cannot see who will be asked.
            logger.LogWarning(ex, "Could not preview the approval route for {From} to {To}",
                string.Join(",", fromWarehouses), string.Join(",", toWarehouses));
            return null;
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
