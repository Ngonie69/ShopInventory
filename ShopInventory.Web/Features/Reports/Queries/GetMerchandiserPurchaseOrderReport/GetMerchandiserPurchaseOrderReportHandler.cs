using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;

public sealed class GetMerchandiserPurchaseOrderReportHandler(
    HttpClient httpClient,
    ILogger<GetMerchandiserPurchaseOrderReportHandler> logger
) : IRequestHandler<GetMerchandiserPurchaseOrderReportQuery, ErrorOr<GetMerchandiserPurchaseOrderReportResult>>
{
    public async Task<ErrorOr<GetMerchandiserPurchaseOrderReportResult>> Handle(
        GetMerchandiserPurchaseOrderReportQuery request,
        CancellationToken cancellationToken)
    {
        try
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

            if (request.MerchandiserUserId.HasValue)
            {
                queryParts.Add($"merchandiserUserId={request.MerchandiserUserId.Value}");
            }

            if (request.HasAttachments.HasValue)
            {
                queryParts.Add($"hasAttachments={request.HasAttachments.Value.ToString().ToLowerInvariant()}");
            }

            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                queryParts.Add($"search={Uri.EscapeDataString(request.Search.Trim())}");
            }

            var url = "api/report/merchandiser-purchase-orders";
            if (queryParts.Count > 0)
            {
                url += "?" + string.Join("&", queryParts);
            }

            var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to load merchandiser purchase order report from {Url}. Status code: {StatusCode}",
                    url,
                    (int)response.StatusCode);
                return Errors.Report.LoadMerchandiserPurchaseOrdersFailed("Failed to load merchandiser purchase order report.");
            }

            var result = await response.Content.ReadFromJsonAsync<GetMerchandiserPurchaseOrderReportResult>(cancellationToken: cancellationToken);
            if (result is null)
            {
                return Errors.Report.LoadMerchandiserPurchaseOrdersFailed("Failed to load merchandiser purchase order report.");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading merchandiser purchase order report in web CQRS handler");
            return Errors.Report.LoadMerchandiserPurchaseOrdersFailed("Failed to load merchandiser purchase order report.");
        }
    }
}