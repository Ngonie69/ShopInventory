using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

public sealed class GetItemVolumeSalesReportHandler(
    HttpClient httpClient,
    ILogger<GetItemVolumeSalesReportHandler> logger
) : IRequestHandler<GetItemVolumeSalesReportQuery, ErrorOr<GetItemVolumeSalesReportResult>>
{
    public async Task<ErrorOr<GetItemVolumeSalesReportResult>> Handle(
        GetItemVolumeSalesReportQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var accountCodes = SplitCodes(request.AccountCodesText);
            if (accountCodes.Count == 0)
            {
                return Errors.Report.LoadItemVolumeSalesFailed("At least one business partner code is required.");
            }

            var itemCodes = SplitCodes(request.ItemCodesText);

            var queryParts = new List<string>
            {
                $"grouping={request.Grouping}"
            };

            if (request.FromDate.HasValue)
            {
                queryParts.Add($"fromDate={request.FromDate.Value:yyyy-MM-dd}");
            }

            if (request.ToDate.HasValue)
            {
                queryParts.Add($"toDate={request.ToDate.Value:yyyy-MM-dd}");
            }

            queryParts.AddRange(accountCodes.Select(code => $"accountCodes={Uri.EscapeDataString(code)}"));
            queryParts.AddRange(itemCodes.Select(code => $"itemCodes={Uri.EscapeDataString(code)}"));

            var url = $"api/report/item-volume-sales?{string.Join("&", queryParts)}";

            logger.LogInformation(
                "Requesting item volume and revenue report for {AccountCount} account(s) and {ItemCount} item(s) between {FromDate} and {ToDate} grouped by {Grouping}",
                accountCodes.Count,
                itemCodes.Count,
                request.FromDate,
                request.ToDate,
                request.Grouping);

            var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Failed to load item volume and revenue report from {Url}. Status code: {StatusCode}. Body: {Body}",
                    url,
                    (int)response.StatusCode,
                    body);

                return Errors.Report.LoadItemVolumeSalesFailed(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "Your session could not access the item volume report. Refresh and try again."
                        : "Failed to load the item volume report.");
            }

            var result = await response.Content.ReadFromJsonAsync<GetItemVolumeSalesReportResult>(
                cancellationToken: cancellationToken);

            if (result is null)
            {
                return Errors.Report.LoadItemVolumeSalesFailed("Failed to load the item volume report.");
            }

            logger.LogInformation(
                "Loaded item volume and revenue report: {ActiveAccountCount} active account(s), {ItemCount} item(s), {MissingFactorCount} item(s) without a conversion factor",
                result.Summary.ActiveAccountCount,
                result.Summary.ItemCount,
                result.Summary.ItemsWithoutFactorCount);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading item volume and revenue report in web CQRS handler");
            return Errors.Report.LoadItemVolumeSalesFailed("Failed to load the item volume report.");
        }
    }

    private static List<string> SplitCodes(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
