using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetStockSummary;

public sealed class GetStockSummaryHandler(
    IReportService reportService,
    ILogger<GetStockSummaryHandler> logger
) : IRequestHandler<GetStockSummaryQuery, ErrorOr<StockSummaryReportDto>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<StockSummaryReportDto>> Handle(
        GetStockSummaryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetStockSummaryAsync(request.WarehouseCode, cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Stock summary report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating stock summary report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }
}
