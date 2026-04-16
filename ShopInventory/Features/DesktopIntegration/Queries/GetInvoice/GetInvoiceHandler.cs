using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoice;

public sealed class GetInvoiceHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetInvoiceQuery, ErrorOr<InvoiceDto>>
{
    public async Task<ErrorOr<InvoiceDto>> Handle(
        GetInvoiceQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var invoice = await sapClient.GetInvoiceByDocEntryAsync(query.DocEntry, cancellationToken);

        if (invoice == null)
            return Errors.DesktopIntegration.InvoiceNotFound(query.DocEntry);

        return invoice.ToDto();
    }
}
