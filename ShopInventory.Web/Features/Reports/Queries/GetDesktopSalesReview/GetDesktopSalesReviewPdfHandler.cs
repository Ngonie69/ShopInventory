using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

public sealed class GetDesktopSalesReviewPdfHandler(HttpClient httpClient, ILogger<GetDesktopSalesReviewPdfHandler> logger)
    : IRequestHandler<GetDesktopSalesReviewPdfQuery, ErrorOr<byte[]>>
{
    public Task<ErrorOr<byte[]>> Handle(GetDesktopSalesReviewPdfQuery request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<byte[]>(
            httpClient, logger, HttpMethod.Get,
            DesktopSalesReviewApi.Base + "/pdf" + DesktopSalesReviewApi.Query(request.FromDate, request.ToDate, request.WarehouseCode),
            null, "build the review PDF", cancellationToken);
}
