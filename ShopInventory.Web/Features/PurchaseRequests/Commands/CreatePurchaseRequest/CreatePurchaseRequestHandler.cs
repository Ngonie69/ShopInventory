using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.PurchaseRequests.Commands.CreatePurchaseRequest;

public sealed class CreatePurchaseRequestHandler(
    IPurchaseRequestService purchaseRequestService,
    ILogger<CreatePurchaseRequestHandler> logger
) : IRequestHandler<CreatePurchaseRequestCommand, ErrorOr<PurchaseRequestDto>>
{
    public async Task<ErrorOr<PurchaseRequestDto>> Handle(
        CreatePurchaseRequestCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdRequest = await purchaseRequestService.CreatePurchaseRequestAsync(request.Request, cancellationToken);

            if (createdRequest is null)
                return Errors.PurchaseRequest.CreateRequestFailed("Failed to create purchase request.");

            return createdRequest;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Purchase request creation request failed");
            return Errors.PurchaseRequest.CreateRequestFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error creating purchase request");
            return Errors.PurchaseRequest.CreateRequestFailed("Failed to create purchase request.");
        }
    }
}