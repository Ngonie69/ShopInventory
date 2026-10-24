using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Reads one report from the API, and on failure says why in words for the screen.
/// </summary>
/// <remarks>
/// The API's own reason where it gave one — a period too long, a rep it does not know — otherwise what
/// kind of failure it was. A cancelled read is the caller's doing and is thrown, not reported. Shared by
/// the van report services so a report page reads every failure the same way.
/// </remarks>
public static class ReportReader
{
    public static async Task<ReportResult<T>> GetAsync<T>(
        HttpClient httpClient,
        ILogger logger,
        string url,
        string what,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                return value is null
                    ? ReportResult<T>.Failure($"The {what} came back empty.")
                    : ReportResult<T>.Success(value);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("The {What} was refused with {Status}", what, (int)response.StatusCode);

            return ReportResult<T>.Failure(ApiErrorResponse.GetFriendlyMessage(
                response.StatusCode,
                body,
                $"The {what} could not be loaded: the server answered {(int)response.StatusCode}."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Could not reach the API for the {What}", what);
            return ReportResult<T>.Failure($"The {what} could not be loaded: the API could not be reached.");
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, "The API did not answer in time for the {What}", what);
            return ReportResult<T>.Failure($"The {what} could not be loaded: the API did not answer in time.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading the {What}", what);
            return ReportResult<T>.Failure($"The {what} could not be read: {ex.Message}");
        }
    }
}
