using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPagedInvoices;

public sealed class GetPagedInvoicesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetPagedInvoicesQuery, ErrorOr<List<InvoiceDto>>>
{
    public async Task<ErrorOr<List<InvoiceDto>>> Handle(
        GetPagedInvoicesQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var invoices = await sapClient.GetPagedInvoicesAsync(page, pageSize, cancellationToken);

        return invoices.ToDto();
    }
}
