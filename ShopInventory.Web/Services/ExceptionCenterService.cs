using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IExceptionCenterService
{
    Task<ExceptionCenterDashboardModel?> GetDashboardAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<bool> RetryItemAsync(string source, string itemKey, CancellationToken cancellationToken = default);
    Task<bool> AcknowledgeItemAsync(string source, string itemKey, CancellationToken cancellationToken = default);
    Task<bool> AssignItemAsync(string source, string itemKey, CancellationToken cancellationToken = default);
}

public sealed class ExceptionCenterService(
    HttpClient httpClient,
    ILogger<ExceptionCenterService> logger
) : IExceptionCenterService
{
    public async Task<ExceptionCenterDashboardModel?> GetDashboardAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ExceptionCenterDashboardModel>($"api/exception-center?limit={limit}", cancellationToken);
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