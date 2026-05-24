using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;

public sealed class GetFiscalTransactionLogHandler(
    HttpClient httpClient,
    ILogger<GetFiscalTransactionLogHandler> logger) : IRequestHandler<GetFiscalTransactionLogQuery, ErrorOr<GetFiscalTransactionLogResult>>
{
    public async Task<ErrorOr<GetFiscalTransactionLogResult>> Handle(
        GetFiscalTransactionLogQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var queryParts = new List<string>
            {
                $"page={Math.Max(1, request.Page)}",
                $"pageSize={Math.Clamp(request.PageSize, 1, 200)}"
            };

            if (request.FromDate.HasValue)
            {
                queryParts.Add($"fromUtc={request.FromDate.Value:yyyy-MM-dd}");
            }

            if (request.ToDate.HasValue)
            {
                queryParts.Add($"toUtc={request.ToDate.Value:yyyy-MM-dd}");
            }

            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                queryParts.Add($"search={Uri.EscapeDataString(request.Search.Trim())}");
            }

            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                queryParts.Add($"status={Uri.EscapeDataString(request.Status.Trim())}");
            }

            if (!string.IsNullOrWhiteSpace(request.DocumentType))
            {
                queryParts.Add($"documentType={Uri.EscapeDataString(request.DocumentType.Trim())}");
            }

            if (!string.IsNullOrWhiteSpace(request.SourceSystem))
            {
                queryParts.Add($"sourceSystem={Uri.EscapeDataString(request.SourceSystem.Trim())}");
            }

            var url = $"api/DesktopIntegration/fiscal-transactions?{string.Join("&", queryParts)}";
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Failed to load fiscal transaction log from {Url}. Status code: {StatusCode}. Body: {Body}",
                    url,
                    (int)response.StatusCode,
                    body);
                return Errors.Report.LoadFiscalTransactionsFailed(
                    response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        ? "Your session could not access the fiscal transaction log. Refresh and try again."
                        : "Failed to load fiscal transaction log.");
            }

            var result = await response.Content.ReadFromJsonAsync<GetFiscalTransactionLogResult>(cancellationToken: cancellationToken);
            if (result is null)
            {
                return Errors.Report.LoadFiscalTransactionsFailed("Failed to load fiscal transaction log.");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading fiscal transaction log in web CQRS handler");
            return Errors.Report.LoadFiscalTransactionsFailed("Failed to load fiscal transaction log.");
        }
    }
}