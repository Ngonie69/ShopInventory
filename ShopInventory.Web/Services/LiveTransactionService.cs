using System.Globalization;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface ILiveTransactionService
{
    /// <summary>
    /// One page of the live feed from <paramref name="sinceUtc"/> (null for the start of today), or null
    /// when the API could not be read.
    /// </summary>
    Task<LiveTransactionFeedModel?> GetFeedAsync(DateTime? sinceUtc, int limit, CancellationToken cancellationToken = default);
}

public sealed class LiveTransactionService(
    HttpClient httpClient,
    ILogger<LiveTransactionService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider
) : ILiveTransactionService
{
    public async Task<LiveTransactionFeedModel?> GetFeedAsync(
        DateTime? sinceUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"api/live-transactions/feed?limit={limit.ToString(CultureInfo.InvariantCulture)}";
            if (sinceUtc is { } since)
            {
                var utc = DateTime.SpecifyKind(since, DateTimeKind.Utc);
                url += "&since=" + Uri.EscapeDataString(utc.ToString("O", CultureInfo.InvariantCulture));
            }

            return await GetAuthenticatedJsonAsync<LiveTransactionFeedModel>(url, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Warning, not Error: the page polls every few seconds and says on screen that it is
            // reconnecting, so an API restart should not read as dozens of errors in the log.
            logger.LogWarning(ex, "Failed to read the live transactions feed");
            return null;
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
