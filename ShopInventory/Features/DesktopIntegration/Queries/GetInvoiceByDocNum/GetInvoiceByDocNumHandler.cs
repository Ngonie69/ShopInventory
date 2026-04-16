using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoiceByDocNum;

public sealed class GetInvoiceByDocNumHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetInvoiceByDocNumQuery, ErrorOr<InvoiceDto>>
{
    public async Task<ErrorOr<InvoiceDto>> Handle(
        GetInvoiceByDocNumQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var invoice = await sapClient.GetInvoiceByDocNumAsync(query.DocNum, cancellationToken);

        if (invoice == null)
            return Errors.DesktopIntegration.InvoiceNotFound(query.DocNum);

        return invoice.ToDto();
    }
}
