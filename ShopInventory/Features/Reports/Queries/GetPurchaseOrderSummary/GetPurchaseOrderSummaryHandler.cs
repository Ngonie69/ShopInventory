using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetPurchaseOrderSummary;

public sealed class GetPurchaseOrderSummaryHandler(
    IReportService reportService,
    ILogger<GetPurchaseOrderSummaryHandler> logger
) : IRequestHandler<GetPurchaseOrderSummaryQuery, ErrorOr<PurchaseOrderSummaryReportDto>>
{
    public async Task<ErrorOr<PurchaseOrderSummaryReportDto>> Handle(
        GetPurchaseOrderSummaryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(request.ToDate ?? DateTime.UtcNow);

            using var cts = ReportDeadline.Start(cancellationToken);
            var result = await reportService.GetPurchaseOrderSummaryAsync(from, to, cts.Token);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller hung up. Nobody is waiting for an answer, so this is not a timeout and
            // not a fault: let RequestCanceledExceptionHandler answer 499 and log its one line.
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Purchase orders report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating purchase orders report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    private static DateTime ToUtc(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
}
