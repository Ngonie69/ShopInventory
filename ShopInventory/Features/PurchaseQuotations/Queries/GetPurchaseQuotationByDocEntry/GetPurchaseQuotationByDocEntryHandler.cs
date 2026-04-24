using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseQuotations;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseQuotations.Queries.GetPurchaseQuotationByDocEntry;

public sealed class GetPurchaseQuotationByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetPurchaseQuotationByDocEntryHandler> logger
) : IRequestHandler<GetPurchaseQuotationByDocEntryQuery, ErrorOr<PurchaseQuotationDto>>
{
    public async Task<ErrorOr<PurchaseQuotationDto>> Handle(
        GetPurchaseQuotationByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var purchaseQuotation = await sapClient.GetPurchaseQuotationByDocEntryAsync(request.DocEntry, cancellationToken);
            if (purchaseQuotation is null)
                return Errors.PurchaseQuotation.NotFoundByDocEntry(request.DocEntry);

            return PurchaseQuotationMappings.MapFromSap(purchaseQuotation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase quotation {DocEntry} from SAP", request.DocEntry);
            return Errors.PurchaseQuotation.SapError(ex.Message);
        }
    }
}