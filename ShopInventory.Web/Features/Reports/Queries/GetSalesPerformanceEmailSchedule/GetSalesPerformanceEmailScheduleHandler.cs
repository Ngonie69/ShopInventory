using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.Reports.Queries.GetSalesPerformanceEmailSchedule;

public sealed class GetSalesPerformanceEmailScheduleHandler(HttpClient httpClient, ILogger<GetSalesPerformanceEmailScheduleHandler> logger)
    : IRequestHandler<GetSalesPerformanceEmailScheduleQuery, ErrorOr<SalesPerformanceEmailSchedule>>
{
    /// <summary>The API's management email endpoints.</summary>
    public const string Base = "api/Report/sales-performance";

    public Task<ErrorOr<SalesPerformanceEmailSchedule>> Handle(GetSalesPerformanceEmailScheduleQuery request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<SalesPerformanceEmailSchedule>(
            httpClient, logger, HttpMethod.Get, Base + "/schedule", null, "load the sales performance email schedule", cancellationToken);
}
