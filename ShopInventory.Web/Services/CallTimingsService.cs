using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface ICallTimingsService
{
    /// <summary>The API's call timings over <paramref name="window"/>, or null when the API could not be asked.</summary>
    Task<CallTimingsModel?> GetAsync(string window, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the API's in-memory timings of its own requests and SAP calls for <c>/call-timings</c>.
/// </summary>
/// <remarks>
/// A failure logs and returns null, like the other thin clients here; the page says the API did not
/// answer, which is a different fault from SAP being slow.
/// </remarks>
public sealed class CallTimingsService(
    HttpClient httpClient,
    ILogger<CallTimingsService> logger) : ICallTimingsService
{
    public async Task<CallTimingsModel?> GetAsync(string window, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<CallTimingsModel>(
                $"api/call-timings?window={Uri.EscapeDataString(window)}",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the API call timings");
            return null;
        }
    }
}
