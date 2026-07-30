using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IExceptionCenterService
{
    Task<ExceptionCenterDashboardModel?> GetDashboardAsync(int limit = 150, string? assignee = null, CancellationToken cancellationToken = default);
    Task<bool> RetryItemAsync(string source, string itemKey, CancellationToken cancellationToken = default);
    Task<ExceptionCenterBatchRetryResultModel?> RetryBatchAsync(IReadOnlyList<ExceptionCenterItemRefModel> items, CancellationToken cancellationToken = default);
    Task<bool> AcknowledgeItemAsync(string source, string itemKey, CancellationToken cancellationToken = default);
    Task<bool> AssignItemAsync(string source, string itemKey, CancellationToken cancellationToken = default);
}

public sealed class ExceptionCenterService(
    HttpClient httpClient,
    ILogger<ExceptionCenterService> logger
) : IExceptionCenterService
{
    public async Task<ExceptionCenterDashboardModel?> GetDashboardAsync(
        int limit = 150,
        string? assignee = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"api/exception-center?limit={limit}";
            if (!string.IsNullOrWhiteSpace(assignee))
            {
                url += $"&assignee={Uri.EscapeDataString(assignee)}";
            }

            return await httpClient.GetFromJsonAsync<ExceptionCenterDashboardModel>(url, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load exception center dashboard");
            return null;
        }
    }

    public async Task<bool> RetryItemAsync(string source, string itemKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsync($"api/exception-center/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(itemKey)}/retry", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retry exception center item {Source}:{ItemKey}", source, itemKey);
            return false;
        }
    }

    public async Task<ExceptionCenterBatchRetryResultModel?> RetryBatchAsync(
        IReadOnlyList<ExceptionCenterItemRefModel> items,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync(
                "api/exception-center/items/retry-batch",
                new ExceptionCenterBatchRetryRequestModel { Items = items.ToList() },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Exception center batch retry returned {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ExceptionCenterBatchRetryResultModel>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to batch retry {Count} exception center items", items.Count);
            return null;
        }
    }

    public async Task<bool> AcknowledgeItemAsync(string source, string itemKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsync($"api/exception-center/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(itemKey)}/acknowledge", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to acknowledge exception center item {Source}:{ItemKey}", source, itemKey);
            return false;
        }
    }

    public async Task<bool> AssignItemAsync(string source, string itemKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsync($"api/exception-center/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(itemKey)}/assign-to-me", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to assign exception center item {Source}:{ItemKey}", source, itemKey);
            return false;
        }
    }
}
