using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetSalesSummary;

public sealed class GetSalesSummaryHandler(
    IReportService reportService,
    ILogger<GetSalesSummaryHandler> logger
) : IRequestHandler<GetSalesSummaryQuery, ErrorOr<SalesSummaryReportDto>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<SalesSummaryReportDto>> Handle(
        GetSalesSummaryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(request.ToDate ?? DateTime.UtcNow);

            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetSalesSummaryAsync(from, to, cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Sales summary report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating sales summary report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    private static DateTime ToUtc(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
}
