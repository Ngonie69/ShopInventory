using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetLowStockAlerts;

public sealed class GetLowStockAlertsHandler(
    IReportService reportService,
    ILogger<GetLowStockAlertsHandler> logger
) : IRequestHandler<GetLowStockAlertsQuery, ErrorOr<LowStockAlertReportDto>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<LowStockAlertReportDto>> Handle(
        GetLowStockAlertsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetLowStockAlertsAsync(request.WarehouseCode, request.Threshold, cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Low stock alerts report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating low stock alerts");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }
}
