using System.Net.Http.Json;
using Blazored.LocalStorage;
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
    ILogger<ExceptionCenterService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider
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

            return await GetAuthenticatedJsonAsync<ExceptionCenterDashboardModel>(url, cancellationToken);
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
            var response = await SendAuthenticatedAsync(() => httpClient.PostAsync($"api/exception-center/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(itemKey)}/retry", null, cancellationToken));
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
            var response = await SendAuthenticatedAsync(() => httpClient.PostAsJsonAsync(
                "api/exception-center/items/retry-batch",
                new ExceptionCenterBatchRetryRequestModel { Items = items.ToList() },
                cancellationToken));

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
            var response = await SendAuthenticatedAsync(() => httpClient.PostAsync($"api/exception-center/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(itemKey)}/acknowledge", null, cancellationToken));
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
            var response = await SendAuthenticatedAsync(() => httpClient.PostAsync($"api/exception-center/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(itemKey)}/assign-to-me", null, cancellationToken));
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to assign exception center item {Source}:{ItemKey}", source, itemKey);
            return false;
        }
    }

    /// <summary>
    /// As the signed-in user for as long as the session can be renewed: a valid access token is
    /// attached first, and one the API rejects is renewed and the call sent once more.
    /// </summary>
    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, sendAsync, logger);

    /// <summary>The body deserialized, or the failure thrown, as <c>GetFromJsonAsync</c> has it.</summary>
    private async Task<T?> GetAuthenticatedJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
