using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesByDateRange;

public sealed class GetInvoicesByDateRangeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings
) : IRequestHandler<GetInvoicesByDateRangeQuery, ErrorOr<List<InvoiceDto>>>
{
    public async Task<ErrorOr<List<InvoiceDto>>> Handle(
        GetInvoicesByDateRangeQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        if (query.FromDate > query.ToDate)
            return Errors.DesktopIntegration.ValidationFailed("fromDate must be before or equal to toDate");

        var invoices = await sapClient.GetInvoicesByDateRangeAsync(
            query.FromDate, query.ToDate, cancellationToken);

        return invoices.ToDto();
    }
}
