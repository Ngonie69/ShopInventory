using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Reads the platform's activity feed: every receipt it archived, every failed or retried attempt and
/// every fiscal day opened or closed, whichever system sent them.
/// </summary>
/// <remarks>
/// A separate typed client from <see cref="IFiscalisationApiClient"/>, as the fiscal-day client is, because
/// the feed needs the <c>activity.read</c> scope that nothing else here does. Keeping it apart means an
/// installation whose key lacks that scope loses the live dashboard's fiscal half and nothing else.
/// </remarks>
public interface IFiscalActivityFeedClient
{
    /// <summary>
    /// Events at or after <paramref name="since"/>, oldest first, at most <paramref name="limit"/>.
    /// </summary>
    /// <exception cref="FiscalisationApiException">Any non-success answer, including a key without the scope.</exception>
    Task<FiscalActivityFeedApiResponse> GetFeedAsync(
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class FiscalActivityFeedClient(
    HttpClient httpClient,
    IOptions<FiscalisationSettings> settings) : IFiscalActivityFeedClient
{
    /// <summary>The platform refuses a larger page.</summary>
    public const int MaxLimit = 500;

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FiscalActivityFeedApiResponse> GetFeedAsync(
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Value.ApiKey))
        {
            throw new FiscalisationApiException(
                HttpStatusCode.Unauthorized,
                FiscalisationApiClient.ApiKeyNotConfiguredErrorCode,
                "No Fiscalisation API key is configured; set Fiscalisation__ApiKey. The request was not sent.");
        }

        var boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        var requestUri = "api/activity/feed"
            + "?since=" + Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture))
            + "&limit=" + boundedLimit.ToString(CultureInfo.InvariantCulture);

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<FiscalActivityFeedApiResponse>(ApiJsonOptions, cancellationToken)
                ?? throw new FiscalisationApiException(response.StatusCode, "EmptyResponse", "The activity feed answered with an empty body.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        ErrorApiResponse? error = null;
        try
        {
            error = JsonSerializer.Deserialize<ErrorApiResponse>(body, ApiJsonOptions);
        }
        catch (JsonException)
        {
            // Not a problem document: an older platform build without the route answers this way.
        }

        var detail = !string.IsNullOrWhiteSpace(error?.Detail)
            ? error.Detail
            : response.ReasonPhrase ?? "The activity feed request failed.";

        throw new FiscalisationApiException(response.StatusCode, error?.ErrorCode, detail, hasProblemDocument: error is not null);
    }
}
