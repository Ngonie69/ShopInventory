using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;
using ShopInventory.Web.Features.Reports.Queries.GetSalesPerformanceEmailSchedule;

namespace ShopInventory.Web.Features.Reports.Commands.SalesPerformanceEmail;

public sealed class SendSalesPerformanceEmailHandler(HttpClient httpClient, ILogger<SendSalesPerformanceEmailHandler> logger)
    : IRequestHandler<SendSalesPerformanceEmailCommand, ErrorOr<SalesPerformanceEmailResult>>
{
    public Task<ErrorOr<SalesPerformanceEmailResult>> Handle(SendSalesPerformanceEmailCommand request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<SalesPerformanceEmailResult>(
            httpClient, logger, HttpMethod.Post, GetSalesPerformanceEmailScheduleHandler.Base + "/email",
            new { request.Cadence, request.FromDate, request.ToDate, request.Recipients },
            "send the sales performance email", cancellationToken);
}
