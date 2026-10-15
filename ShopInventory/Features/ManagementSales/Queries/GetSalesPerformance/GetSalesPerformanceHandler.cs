using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Services;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;

public sealed class GetSalesPerformanceHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IOptions<TaxSettings> tax,
    ISAPServiceLayerClient sap,
    ILogger<GetSalesPerformanceHandler> logger)
    : IRequestHandler<GetSalesPerformanceQuery, ErrorOr<SalesPerformance>>
{
    public async Task<ErrorOr<SalesPerformance>> Handle(GetSalesPerformanceQuery request, CancellationToken cancellationToken)
    {
        var review = await mediator.Send(
            new GetDesktopSalesReviewQuery(request.CallerUserId, request.FromDate.Date, request.ToDate.Date),
            cancellationToken);
        if (review.IsError)
        {
            return review.Errors;
        }

        return await SalesPerformanceReader.ForReviewAsync(mediator, db, tax.Value, sap, logger, review.Value, cancellationToken);
    }
}
