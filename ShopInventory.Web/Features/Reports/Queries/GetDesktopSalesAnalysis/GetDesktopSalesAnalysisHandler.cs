using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

public sealed class GetDesktopSalesAnalysisHandler(
    HttpClient httpClient,
    ILogger<GetDesktopSalesAnalysisHandler> logger
) : IRequestHandler<GetDesktopSalesAnalysisQuery, ErrorOr<DesktopSalesAnalysisResult>>
{
    public async Task<ErrorOr<DesktopSalesAnalysisResult>> Handle(
        GetDesktopSalesAnalysisQuery request,
        CancellationToken cancellationToken)
    {
        var queryParts = new List<string>();

        if (request.FromDate.HasValue)
        {
            queryParts.Add($"fromDate={request.FromDate.Value:yyyy-MM-dd}");
        }

        if (request.ToDate.HasValue)
        {
            queryParts.Add($"toDate={request.ToDate.Value:yyyy-MM-dd}");
        }

        if (!string.IsNullOrWhiteSpace(request.WarehouseCode))
        {
            queryParts.Add($"warehouseCode={Uri.EscapeDataString(request.WarehouseCode.Trim())}");
        }

        var url = queryParts.Count == 0
            ? "api/DesktopIntegration/sales/analysis"
            : $"api/DesktopIntegration/sales/analysis?{string.Join("&", queryParts)}";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadProblemDetailAsync(response, cancellationToken);

                logger.LogWarning(
                    "Failed to load the desktop sales analysis from {Url}. Status code: {StatusCode}. Detail: {Detail}",
                    url,
                    (int)response.StatusCode,
                    detail);

                // The API's own words where it gave some. A refused warehouse and a period that is too
                // long are both things the operator can act on, and "failed to load" is not.
                return Errors.Report.LoadDesktopSalesAnalysisFailed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized =>
                        "Your session could not access the desktop sales analysis. Refresh and try again.",
                    HttpStatusCode.Forbidden or HttpStatusCode.BadRequest when detail is not null => detail!,
                    HttpStatusCode.Forbidden => "This account is not permitted to read these sales.",
                    _ => "Failed to load the desktop sales analysis."
                });
            }

            var result = await response.Content.ReadFromJsonAsync<DesktopSalesAnalysisResult>(
                cancellationToken: cancellationToken);

            if (result is null)
            {
                return Errors.Report.LoadDesktopSalesAnalysisFailed("Failed to load the desktop sales analysis.");
            }

            logger.LogInformation(
                "Loaded desktop sales analysis from {FromDate:yyyy-MM-dd} to {ToDate:yyyy-MM-dd}: {CurrencyCount} currency section(s), {SalesCount} sale(s)",
                result.FromDate,
                result.ToDate,
                result.Currencies.Count,
                result.Currencies.Sum(currency => currency.SalesCount));

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading the desktop sales analysis from {Url}", url);
            return Errors.Report.LoadDesktopSalesAnalysisFailed("Failed to load the desktop sales analysis.");
        }
    }

    /// <summary>
    /// The first message a problem response carries — a validation message, else its detail.
    /// </summary>
    private static async Task<string?> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
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
