using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

public sealed class GetDesktopSalesReviewScheduleHandler(HttpClient httpClient, ILogger<GetDesktopSalesReviewScheduleHandler> logger)
    : IRequestHandler<GetDesktopSalesReviewScheduleQuery, ErrorOr<DesktopSalesReviewSchedule>>
{
    public Task<ErrorOr<DesktopSalesReviewSchedule>> Handle(GetDesktopSalesReviewScheduleQuery request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<DesktopSalesReviewSchedule>(
            httpClient, logger, HttpMethod.Get, DesktopSalesReviewApi.Base + "/schedule", null, "load the review email schedule", cancellationToken);
}
