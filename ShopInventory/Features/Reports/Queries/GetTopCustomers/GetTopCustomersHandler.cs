using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetTopCustomers;

public sealed class GetTopCustomersHandler(
    IReportService reportService,
    ILogger<GetTopCustomersHandler> logger
) : IRequestHandler<GetTopCustomersQuery, ErrorOr<TopCustomersReportDto>>
{
    public async Task<ErrorOr<TopCustomersReportDto>> Handle(
        GetTopCustomersQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(request.ToDate ?? DateTime.UtcNow);

            using var cts = ReportDeadline.Start(cancellationToken);
            var result = await reportService.GetTopCustomersAsync(from, to, request.TopCount, cts.Token);
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
            logger.LogWarning("Top customers report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating top customers report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    private static DateTime ToUtc(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
}
