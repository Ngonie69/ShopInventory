using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseRequests;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseRequests.Queries.GetPurchaseRequestByDocEntry;

public sealed class GetPurchaseRequestByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetPurchaseRequestByDocEntryHandler> logger
) : IRequestHandler<GetPurchaseRequestByDocEntryQuery, ErrorOr<PurchaseRequestDto>>
{
    public async Task<ErrorOr<PurchaseRequestDto>> Handle(
        GetPurchaseRequestByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var purchaseRequest = await sapClient.GetPurchaseRequestByDocEntryAsync(request.DocEntry, cancellationToken);
            if (purchaseRequest is null)
                return Errors.PurchaseRequest.NotFoundByDocEntry(request.DocEntry);

            return PurchaseRequestMappings.MapFromSap(purchaseRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase request {DocEntry} from SAP", request.DocEntry);
            return Errors.PurchaseRequest.SapError(ex.Message);
        }
    }
}