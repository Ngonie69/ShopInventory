using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesByCustomer;

public sealed class GetInvoicesByCustomerHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    ApplicationDbContext db
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

        var dtos = invoices.ToList().ToDto();

        // The till's invoice history is this list. Without the credits it showed a returned invoice in
        // full, and its figures counted money the shop had given back.
        var creditByDocEntry = await SaleCredits.ForInvoicesAsync(
            db, dtos.Select(invoice => invoice.DocEntry), cancellationToken);

        foreach (var invoice in dtos)
        {
            SaleCredits.ApplyTo(invoice, creditByDocEntry.GetValueOrDefault(invoice.DocEntry));
        }

        return dtos;
    }
}
