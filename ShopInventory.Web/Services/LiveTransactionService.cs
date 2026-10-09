using System.Globalization;
using System.Net.Http.Json;
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
    ILogger<LiveTransactionService> logger
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

            return await httpClient.GetFromJsonAsync<LiveTransactionFeedModel>(url, cancellationToken);
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
}
