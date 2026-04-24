using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseInvoices;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseInvoices.Queries.GetPurchaseInvoiceByDocEntry;

public sealed class GetPurchaseInvoiceByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetPurchaseInvoiceByDocEntryHandler> logger
) : IRequestHandler<GetPurchaseInvoiceByDocEntryQuery, ErrorOr<PurchaseInvoiceDto>>
{
    public async Task<ErrorOr<PurchaseInvoiceDto>> Handle(
        GetPurchaseInvoiceByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await sapClient.GetPurchaseInvoiceByDocEntryAsync(request.DocEntry, cancellationToken);
            if (invoice is null)
                return Errors.PurchaseInvoice.NotFoundByDocEntry(request.DocEntry);

            return PurchaseInvoiceMappings.MapFromSap(invoice);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase invoice {DocEntry} from SAP", request.DocEntry);
            return Errors.PurchaseInvoice.SapError(ex.Message);
        }
    }
}