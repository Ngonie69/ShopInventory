using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Common.Http;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

/// <summary>
/// The one way the portal calls the business review's endpoints, so a refusal reads the same wherever
/// it happens — in the API's own words where it gave some.
/// </summary>
internal static class DesktopSalesReviewApi
{
    public const string Base = "api/DesktopIntegration/sales/review";

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

                return Errors.Report.DesktopSalesReviewFailed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Your session has expired. Refresh and try again.",
                    HttpStatusCode.Forbidden when detail is null => "This account is not permitted to do that.",
                    _ => detail ?? failed
                });
            }

            if (typeof(T) == typeof(byte[]))
            {
                return (T)(object)await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return result is null ? Errors.Report.DesktopSalesReviewFailed(failed) : result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not {What} at {Url}", what, url);
            return Errors.Report.DesktopSalesReviewFailed(failed);
        }
    }

    public static string Query(DateTime? from, DateTime? to, string? warehouse)
    {
        var parts = new List<string>();
        if (from is { } start)
        {
            parts.Add($"fromDate={start:yyyy-MM-dd}");
        }

        if (to is { } end)
        {
            parts.Add($"toDate={end:yyyy-MM-dd}");
        }

        if (!string.IsNullOrWhiteSpace(warehouse))
        {
            parts.Add($"warehouseCode={Uri.EscapeDataString(warehouse.Trim())}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
