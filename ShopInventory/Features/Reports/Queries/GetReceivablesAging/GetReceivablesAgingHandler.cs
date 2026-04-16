using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetReceivablesAging;

public sealed class GetReceivablesAgingHandler(
    IReportService reportService,
    ILogger<GetReceivablesAgingHandler> logger
) : IRequestHandler<GetReceivablesAgingQuery, ErrorOr<ReceivablesAgingReportDto>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<ReceivablesAgingReportDto>> Handle(
        GetReceivablesAgingQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetReceivablesAgingAsync(cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Receivables aging report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating receivables aging report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }
}
