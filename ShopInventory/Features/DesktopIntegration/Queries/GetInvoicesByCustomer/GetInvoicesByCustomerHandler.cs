using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesByCustomer;

public sealed class GetInvoicesByCustomerHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetInvoicesByCustomerQuery, ErrorOr<List<InvoiceDto>>>
{
    public async Task<ErrorOr<List<InvoiceDto>>> Handle(
        GetInvoicesByCustomerQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        Models.Invoice[] invoices;
        if (query.FromDate.HasValue && query.ToDate.HasValue)
        {
            var list = await sapClient.GetInvoicesByCustomerAsync(
                query.CardCode, query.FromDate.Value, query.ToDate.Value, cancellationToken);
            invoices = list.ToArray();
        }
        else
        {
            var list = await sapClient.GetInvoicesByCustomerAsync(query.CardCode, cancellationToken);
            invoices = list.ToArray();
        }

        return invoices.ToList().ToDto();
    }
}
