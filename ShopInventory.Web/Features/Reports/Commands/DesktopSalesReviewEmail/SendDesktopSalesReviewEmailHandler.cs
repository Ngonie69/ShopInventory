using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.Reports.Commands.DesktopSalesReviewEmail;

public sealed class SendDesktopSalesReviewEmailHandler(HttpClient httpClient, ILogger<SendDesktopSalesReviewEmailHandler> logger)
    : IRequestHandler<SendDesktopSalesReviewEmailCommand, ErrorOr<DesktopSalesReviewEmailResult>>
{
    public Task<ErrorOr<DesktopSalesReviewEmailResult>> Handle(SendDesktopSalesReviewEmailCommand request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<DesktopSalesReviewEmailResult>(
            httpClient, logger, HttpMethod.Post, DesktopSalesReviewApi.Base + "/email",
            new { request.Cadence, request.FromDate, request.ToDate, request.Recipients },
            "send the sales review", cancellationToken);
}
