using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetSlowMovingProducts;

public sealed class GetSlowMovingProductsHandler(
    IReportService reportService,
    ILogger<GetSlowMovingProductsHandler> logger
) : IRequestHandler<GetSlowMovingProductsQuery, ErrorOr<SlowMovingProductsReportDto>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<SlowMovingProductsReportDto>> Handle(
        GetSlowMovingProductsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(request.FromDate ?? DateTime.UtcNow.AddDays(-90));
            var to = ToUtc(request.ToDate ?? DateTime.UtcNow);

            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetSlowMovingProductsAsync(from, to, request.DaysThreshold, cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Slow moving products report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating slow moving products report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    private static DateTime ToUtc(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
}
