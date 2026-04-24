using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseRequests;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseRequests.Commands.CreatePurchaseRequest;

public sealed class CreatePurchaseRequestHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<CreatePurchaseRequestHandler> logger
) : IRequestHandler<CreatePurchaseRequestCommand, ErrorOr<PurchaseRequestDto>>
{
    public async Task<ErrorOr<PurchaseRequestDto>> Handle(
        CreatePurchaseRequestCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var purchaseRequest = await sapClient.CreatePurchaseRequestAsync(command.Request, cancellationToken);
            return PurchaseRequestMappings.MapFromSap(purchaseRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase request");
            return Errors.PurchaseRequest.CreationFailed(ex.Message);
        }
    }
}