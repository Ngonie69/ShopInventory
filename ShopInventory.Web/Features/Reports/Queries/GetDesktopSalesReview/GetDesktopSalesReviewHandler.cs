using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

public sealed class GetDesktopSalesReviewHandler(HttpClient httpClient, ILogger<GetDesktopSalesReviewHandler> logger)
    : IRequestHandler<GetDesktopSalesReviewQuery, ErrorOr<DesktopSalesReview>>
{
    public Task<ErrorOr<DesktopSalesReview>> Handle(GetDesktopSalesReviewQuery request, CancellationToken cancellationToken) =>
        DesktopSalesReviewApi.SendAsync<DesktopSalesReview>(
            httpClient, logger, HttpMethod.Get,
            DesktopSalesReviewApi.Base + DesktopSalesReviewApi.Query(request.FromDate, request.ToDate, request.WarehouseCode),
            null, "load the sales review", cancellationToken);
}
