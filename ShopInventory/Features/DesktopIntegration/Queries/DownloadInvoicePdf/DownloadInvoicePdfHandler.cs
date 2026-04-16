using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.DownloadInvoicePdf;

public sealed class DownloadInvoicePdfHandler(
    ISAPServiceLayerClient sapClient,
    IInvoicePdfService invoicePdfService,
    IOptions<SAPSettings> sapSettings,
    ILogger<DownloadInvoicePdfHandler> logger
) : IRequestHandler<DownloadInvoicePdfQuery, ErrorOr<InvoicePdfResult>>
{
    public async Task<ErrorOr<InvoicePdfResult>> Handle(
        DownloadInvoicePdfQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.DesktopIntegration.SapDisabled;

        var invoice = await sapClient.GetInvoiceByDocEntryAsync(query.DocEntry, cancellationToken);

        if (invoice == null)
            return Errors.DesktopIntegration.InvoiceNotFound(query.DocEntry);

        var invoiceDto = invoice.ToDto();

        // Enrich with business partner details
        if (!string.IsNullOrEmpty(invoice.CardCode))
        {
            try
            {
                var bp = await sapClient.GetBusinessPartnerByCodeAsync(invoice.CardCode, cancellationToken);
                if (bp != null)
                {
                    invoiceDto.CustomerVatNo = bp.VatRegNo;
                    invoiceDto.CustomerTinNumber = bp.TinNumber;
                    invoiceDto.CustomerPhone = bp.Phone1;
                    invoiceDto.CustomerEmail = bp.Email;
                }
            }
            catch (Exception bpEx)
            {
                logger.LogWarning(bpEx, "Could not fetch business partner {CardCode} for PDF enrichment",
                    invoice.CardCode);
            }
        }

        var pdfBytes = await invoicePdfService.GenerateInvoicePdfAsync(invoiceDto);
        var fileName = $"Invoice_{invoiceDto.DocNum}_{DateTime.Now:yyyyMMdd}.pdf";

        return new InvoicePdfResult(pdfBytes, fileName);
    }
}
