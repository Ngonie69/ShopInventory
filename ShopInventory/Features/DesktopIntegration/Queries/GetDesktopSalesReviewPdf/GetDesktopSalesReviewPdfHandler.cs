using ErrorOr;
using MediatR;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;

public sealed class GetDesktopSalesReviewPdfHandler(IMediator mediator)
    : IRequestHandler<GetDesktopSalesReviewPdfQuery, ErrorOr<DesktopSalesReviewDocument>>
{
    public async Task<ErrorOr<DesktopSalesReviewDocument>> Handle(
        GetDesktopSalesReviewPdfQuery request, CancellationToken cancellationToken)
    {
        var review = await mediator.Send(
            new GetDesktopSalesReviewQuery(request.CallerUserId, request.FromDate, request.ToDate, request.WarehouseCode),
            cancellationToken);
        if (review.IsError)
        {
            return review.Errors;
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? "Desktop sales review" : request.Title.Trim();
        var content = DesktopSalesReviewPdfRenderer.Render(review.Value, title, AuditService.ToCAT(review.Value.GeneratedAtUtc));
        var scope = review.Value.WarehouseCode is { } warehouse ? $"_{warehouse}" : string.Empty;

        return new DesktopSalesReviewDocument(
            content,
            $"Desktop_Sales_Review{scope}_{review.Value.FromDate:yyyyMMdd}_{review.Value.ToDate:yyyyMMdd}.pdf",
            review.Value);
    }
}
