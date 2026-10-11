using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services.SlHealth;

/// <summary>
/// SL Health's <c>GET /api/integration/status</c> over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Everything that can go wrong here ends in "no answer", never in an exception: SL Health is an
/// advisor, and an advisor that cannot be reached must leave the API doing what it did without one.
/// </para>
/// <para>
/// The token goes out as a bearer header on each request and appears nowhere else. No failure text
/// built here includes it, the request headers, or the response body.
/// </para>
/// </remarks>
public sealed class SlHealthClient(
    HttpClient httpClient,
    IOptions<SlHealthSettings> settings) : ISlHealthClient
{
    /// <summary>How long SL Health gets to answer. It answers from memory, so a slow answer is a sick SL Health.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>Far above a real answer, which is about a kilobyte.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SlHealthAnswer> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var options = settings.Value;
        if (options.BaseAddress() is not { } baseAddress)
        {
            return SlHealthAnswer.Failed("SL Health is not configured.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, "api/integration/status"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return SlHealthAnswer.Failed($"SL Health answered {(int)response.StatusCode}.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var status = await JsonSerializer.DeserializeAsync<SlHealthStatus>(body, Json, cancellationToken);

            return status switch
            {
                null => SlHealthAnswer.Failed("SL Health answered with nothing."),
                { Version: > SlHealthStatus.SupportedVersion } =>
                    SlHealthAnswer.Failed($"SL Health answered in version {status.Version} of its status, which this build cannot read."),
                _ => SlHealthAnswer.Of(status)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SlHealthAnswer.Failed($"SL Health did not answer within {Timeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or NotSupportedException or IOException)
        {
            // The type only. An exception message here can quote a header or a body, and neither belongs in a log.
            return SlHealthAnswer.Failed($"SL Health could not be read ({ex.GetType().Name}).");
        }
    }
}
