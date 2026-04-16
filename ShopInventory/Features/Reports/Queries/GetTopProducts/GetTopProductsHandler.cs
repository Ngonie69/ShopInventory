using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetTopProducts;

public sealed class GetTopProductsHandler(
    IReportService reportService,
    ILogger<GetTopProductsHandler> logger
) : IRequestHandler<GetTopProductsQuery, ErrorOr<TopProductsReportDto>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<TopProductsReportDto>> Handle(
        GetTopProductsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(request.ToDate ?? DateTime.UtcNow);

            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetTopProductsAsync(from, to, request.TopCount, request.WarehouseCode, cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Top products report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating top products report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    private static DateTime ToUtc(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
}
