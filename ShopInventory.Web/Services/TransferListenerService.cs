using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface ITransferListenerService
{
    Task<TransferListenerStatusModel?> GetStatusAsync(
        int recentDocumentCount = 20,
        CancellationToken cancellationToken = default);

    Task<TransferListenerCheckModel?> TriggerCheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the API's view of TransferEventListener for <c>/transfer-listener</c>.
/// </summary>
/// <remarks>
/// The Web never talks to the listener directly — the API owns that link, holds its configuration and
/// is the process whose snapshot the listener feeds.
///
/// A failure here logs and returns null, matching the other client services on this side, and the
/// page distinguishes that from a listener the API reached and found down. The two look alike on
/// screen and are not the same fault: one is "we could not ask", the other is "we asked, and it is
/// broken".
/// </remarks>
public sealed class TransferListenerService(
    HttpClient httpClient,
    ILogger<TransferListenerService> logger) : ITransferListenerService
{
    public async Task<TransferListenerStatusModel?> GetStatusAsync(
        int recentDocumentCount = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<TransferListenerStatusModel>(
                $"api/DesktopIntegration/transfer-listener/status?recentDocumentCount={recentDocumentCount}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the transfer listener status");
            return null;
        }
    }

    public async Task<TransferListenerCheckModel?> TriggerCheckAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsync(
                "api/DesktopIntegration/transfer-listener/check-now", null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Transfer listener check-now returned {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TransferListenerCheckModel>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to trigger a transfer listener check");
            return null;
        }
    }
}
