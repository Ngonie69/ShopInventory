using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class GetAccountSalesPaymentReportHandler(
    HttpClient httpClient,
    ILogger<GetAccountSalesPaymentReportHandler> logger
) : IRequestHandler<GetAccountSalesPaymentReportQuery, ErrorOr<GetAccountSalesPaymentReportResult>>
{
    public async Task<ErrorOr<GetAccountSalesPaymentReportResult>> Handle(
        GetAccountSalesPaymentReportQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var accountCodes = SplitAccountCodes(request.AccountCodesText);
            if (accountCodes.Count == 0)
            {
                return Errors.Report.LoadAccountSalesPaymentsFailed("At least one account code is required.");
            }

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

            foreach (var accountCode in accountCodes)
            {
                queryParts.Add($"accountCodes={Uri.EscapeDataString(accountCode)}");
            }

            var url = $"api/report/account-sales-payments?{string.Join("&", queryParts)}";
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Failed to load account sales and incoming payment report from {Url}. Status code: {StatusCode}. Body: {Body}",
                    url,
                    (int)response.StatusCode,
                    body);

                return Errors.Report.LoadAccountSalesPaymentsFailed(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "Your session could not access the account sales and incoming payment report. Refresh and try again."
                        : "Failed to load the account sales and incoming payment report.");
            }

            var result = await response.Content.ReadFromJsonAsync<GetAccountSalesPaymentReportResult>(cancellationToken: cancellationToken);
            if (result is null)
            {
                return Errors.Report.LoadAccountSalesPaymentsFailed("Failed to load the account sales and incoming payment report.");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading account sales and incoming payment report in web CQRS handler");
            return Errors.Report.LoadAccountSalesPaymentsFailed("Failed to load the account sales and incoming payment report.");
        }
    }

    private static List<string> SplitAccountCodes(string accountCodesText) =>
        accountCodesText
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}