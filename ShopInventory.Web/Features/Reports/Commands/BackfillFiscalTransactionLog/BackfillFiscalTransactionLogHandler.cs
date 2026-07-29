using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Reports.Commands.BackfillFiscalTransactionLog;

public sealed class BackfillFiscalTransactionLogHandler(
    IHttpClientFactory httpClientFactory,
    ILogger<BackfillFiscalTransactionLogHandler> logger) : IRequestHandler<BackfillFiscalTransactionLogCommand, ErrorOr<BackfillFiscalTransactionLogResult>>
{
    public async Task<ErrorOr<BackfillFiscalTransactionLogResult>> Handle(
        BackfillFiscalTransactionLogCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("ShopInventoryApiLongRunning");
            var payload = new
            {
                FromUtc = request.FromDate,
                ToUtc = request.ToDate,
                request.MaxInvoices,
                request.PageSize
            };

            using var response = await client.PostAsJsonAsync(
                "api/DesktopIntegration/fiscal-transactions/backfill",
                payload,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Fiscal transaction backfill failed with status {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    ApiErrorResponse.SanitizeForLog(body));

                return Errors.Report.BackfillFiscalTransactionsFailed(
                    ApiErrorResponse.GetFriendlyMessage(
                        response.StatusCode,
                        body,
                        "Failed to backfill fiscalised invoices."));
            }

            var result = await response.Content.ReadFromJsonAsync<BackfillFiscalTransactionLogResult>(cancellationToken: cancellationToken);
            if (result is null)
            {
                return Errors.Report.BackfillFiscalTransactionsFailed("Failed to read the fiscal transaction backfill result.");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to backfill fiscal transaction log from the web report");
            return Errors.Report.BackfillFiscalTransactionsFailed(
                ApiErrorResponse.GetFriendlyMessage(ex, "Failed to backfill fiscalised invoices."));
        }
    }
}