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
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<PurchaseOrderSummaryReportDto>> Handle(
        GetPurchaseOrderSummaryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(request.ToDate ?? DateTime.UtcNow);

            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetPurchaseOrderSummaryAsync(from, to, cts.Token);
            return result;
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
