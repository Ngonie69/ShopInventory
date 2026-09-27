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

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoice;

public sealed class GetInvoiceHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    ApplicationDbContext db
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

        var dto = invoice.ToDto();

        // The till's invoice panel draws these lines, and lets the cashier narrow them to the ones the
        // office credited back.
        var credits = await SaleCredits.ForInvoicesAsync(db, [dto.DocEntry], cancellationToken);
        SaleCredits.ApplyTo(dto, credits.GetValueOrDefault(dto.DocEntry));

        return dto;
    }
}
