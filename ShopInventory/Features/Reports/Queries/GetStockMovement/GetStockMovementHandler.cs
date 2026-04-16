using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetStockMovement;

public sealed class GetStockMovementHandler(
    IReportService reportService,
    ILogger<GetStockMovementHandler> logger
) : IRequestHandler<GetStockMovementQuery, ErrorOr<StockMovementReportDto>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<StockMovementReportDto>> Handle(
        GetStockMovementQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(request.ToDate ?? DateTime.UtcNow);

            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetStockMovementAsync(from, to, request.WarehouseCode, cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Stock movement report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating stock movement report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    private static DateTime ToUtc(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
}
