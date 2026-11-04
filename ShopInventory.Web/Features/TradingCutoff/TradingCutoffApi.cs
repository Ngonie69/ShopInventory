using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Common.Http;

namespace ShopInventory.Web.Features.TradingCutoff;

/// <summary>
/// The one way the portal calls the trading cut-off, so a refusal reads the same wherever it happens: in the
/// API's own words where it gave some.
/// </summary>
internal static class TradingCutoffApi
{
    public const string Url = "api/trading-cutoff";

    public static async Task<ErrorOr<TradingCutoffSettings>> SendAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        object? body,
        string what,
        CancellationToken cancellationToken)
    {
        var failed = $"Could not {what}.";

        try
        {
            using var request = new HttpRequestMessage(method, Url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ProblemDetailReader.ReadMessageAsync(response, cancellationToken);
                logger.LogWarning("Could not {What}: {Url} answered {StatusCode}. {Detail}", what, Url, (int)response.StatusCode, detail);

                return Errors.TradingCutoff.Failed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Your session has expired. Refresh and try again.",
                    HttpStatusCode.Forbidden when detail is null => "Only an administrator can change this.",
                    _ => detail ?? failed
                });
            }

            var result = await response.Content.ReadFromJsonAsync<TradingCutoffSettings>(cancellationToken: cancellationToken);
            return result is null ? Errors.TradingCutoff.Failed(failed) : result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not {What} at {Url}", what, Url);
            return Errors.TradingCutoff.Failed(failed);
        }
    }
}
