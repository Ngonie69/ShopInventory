using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.PurchaseRequests.Queries.GetPurchaseRequests;

public sealed class GetPurchaseRequestsHandler(
    IPurchaseRequestService purchaseRequestService,
    ILogger<GetPurchaseRequestsHandler> logger
) : IRequestHandler<GetPurchaseRequestsQuery, ErrorOr<PurchaseRequestListResponse>>
{
    public async Task<ErrorOr<PurchaseRequestListResponse>> Handle(
        GetPurchaseRequestsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await purchaseRequestService.GetPurchaseRequestsAsync(
                request.Page,
                request.PageSize,
                request.FromDate,
                request.ToDate,
                cancellationToken);

            if (response is null)
                return Errors.PurchaseRequest.LoadRequestsFailed("Failed to load purchase requests.");

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase requests in web CQRS handler");
            return Errors.PurchaseRequest.LoadRequestsFailed("Failed to load purchase requests.");
        }
    }
}