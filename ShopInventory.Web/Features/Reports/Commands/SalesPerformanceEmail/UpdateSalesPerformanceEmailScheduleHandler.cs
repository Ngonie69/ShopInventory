using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;
using ShopInventory.Web.Features.Reports.Queries.GetSalesPerformanceEmailSchedule;

namespace ShopInventory.Web.Features.Reports.Commands.SalesPerformanceEmail;

public sealed class UpdateSalesPerformanceEmailScheduleHandler(HttpClient httpClient, ILogger<UpdateSalesPerformanceEmailScheduleHandler> logger)
    : IRequestHandler<UpdateSalesPerformanceEmailScheduleCommand, ErrorOr<SalesPerformanceEmailSchedule>>
{
    public Task<ErrorOr<SalesPerformanceEmailSchedule>> Handle(UpdateSalesPerformanceEmailScheduleCommand request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<SalesPerformanceEmailSchedule>(
            httpClient, logger, HttpMethod.Put, GetSalesPerformanceEmailScheduleHandler.Base + "/schedule",
            new { request.DailyEnabled, request.WeeklyEnabled, request.MonthlyEnabled, request.Recipients },
            "save the sales performance email schedule", cancellationToken);
}
