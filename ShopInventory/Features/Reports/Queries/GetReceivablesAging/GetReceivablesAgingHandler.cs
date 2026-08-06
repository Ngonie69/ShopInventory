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
    public async Task<ErrorOr<ReceivablesAgingReportDto>> Handle(
        GetReceivablesAgingQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = ReportDeadline.Start(cancellationToken);
            var result = await reportService.GetReceivablesAgingAsync(cts.Token);
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
