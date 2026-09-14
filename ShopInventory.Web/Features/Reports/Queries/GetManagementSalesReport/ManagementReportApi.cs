using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;

/// <summary>
/// The one way the portal reads the management sales report's endpoints, so the report and its item
/// drill-down pass the same filters and turn a refusal into the same words.
/// </summary>
internal static class ManagementReportApi
{
    public static async Task<ErrorOr<T>> GetAsync<T>(
        HttpClient httpClient,
        ILogger logger,
        string path,
        string what,
        IEnumerable<(string Key, string? Value)> parameters,
        CancellationToken cancellationToken)
    {
        var query = string.Join("&", parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{parameter.Key}={Uri.EscapeDataString(parameter.Value!.Trim())}"));
        var url = query.Length == 0 ? path : $"{path}?{query}";
        var failed = $"Failed to load the {what}.";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadProblemDetailAsync(response, cancellationToken);

                logger.LogWarning(
                    "Failed to load the {What} from {Url}. Status code: {StatusCode}. Detail: {Detail}",
                    what,
                    url,
                    (int)response.StatusCode,
                    detail);

                return Errors.Report.LoadManagementSalesReportFailed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => $"Your session could not access the {what}. Refresh and try again.",
                    HttpStatusCode.Forbidden or HttpStatusCode.BadRequest when detail is not null => detail!,
                    HttpStatusCode.Forbidden => "This account is not permitted to read these sales.",
                    _ => failed
                });
            }

            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return result is null ? Errors.Report.LoadManagementSalesReportFailed(failed) : result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading the {What} from {Url}", what, url);
            return Errors.Report.LoadManagementSalesReportFailed(failed);
        }
    }

    public static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The first message a problem response carries — a validation message, else its detail.</summary>
    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errors.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var message in field.Value.EnumerateArray())
                    {
                        if (message.ValueKind == JsonValueKind.String)
                        {
                            return message.GetString();
                        }
                    }
                }
            }

            return root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                ? detail.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
