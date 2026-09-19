using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Common.Http;

namespace ShopInventory.Web.Features.DailyIncomingPayments;

/// <summary>
/// The one way the portal calls the daily incoming payment endpoints, so a refusal reads the same wherever
/// it happens: in the API's own words where it gave some.
/// </summary>
internal static class DailyIncomingPaymentsApi
{
    public const string Base = "api/daily-incoming-payments";

    public static async Task<ErrorOr<T>> SendAsync<T>(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string url,
        object? body,
        string what,
        CancellationToken cancellationToken)
    {
        var failed = $"Could not {what}.";

        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ProblemDetailReader.ReadMessageAsync(response, cancellationToken);
                logger.LogWarning("Could not {What}: {Url} answered {StatusCode}. {Detail}", what, url, (int)response.StatusCode, detail);

                return Errors.DailyIncomingPayment.Failed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Your session has expired. Refresh and try again.",
                    HttpStatusCode.Forbidden when detail is null => "This account is not permitted to do that.",
                    _ => detail ?? failed
                });
            }

            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return result is null ? Errors.DailyIncomingPayment.Failed(failed) : result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not {What} at {Url}", what, url);
            return Errors.DailyIncomingPayment.Failed(failed);
        }
    }
}
