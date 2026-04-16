using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetProfitOverview;

public sealed class GetProfitOverviewHandler(
    IReportService reportService,
    ILogger<GetProfitOverviewHandler> logger
) : IRequestHandler<GetProfitOverviewQuery, ErrorOr<ProfitOverviewReportDto>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<ProfitOverviewReportDto>> Handle(
        GetProfitOverviewQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(request.ToDate ?? DateTime.UtcNow);

            using var cts = new CancellationTokenSource(ReportTimeout);
            var result = await reportService.GetProfitOverviewAsync(from, to, cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Profit overview report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating profit overview report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    private static DateTime ToUtc(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
}
