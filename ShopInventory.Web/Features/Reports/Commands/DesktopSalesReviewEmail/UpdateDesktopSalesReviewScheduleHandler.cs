using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.Reports.Commands.DesktopSalesReviewEmail;

public sealed class UpdateDesktopSalesReviewScheduleHandler(HttpClient httpClient, ILogger<UpdateDesktopSalesReviewScheduleHandler> logger)
    : IRequestHandler<UpdateDesktopSalesReviewScheduleCommand, ErrorOr<DesktopSalesReviewSchedule>>
{
    public Task<ErrorOr<DesktopSalesReviewSchedule>> Handle(UpdateDesktopSalesReviewScheduleCommand request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<DesktopSalesReviewSchedule>(
            httpClient, logger, HttpMethod.Put, DesktopSalesReviewApi.Base + "/schedule",
            new { request.WeeklyEnabled, request.MonthlyEnabled, request.Recipients },
            "save the review email schedule", cancellationToken);
}
