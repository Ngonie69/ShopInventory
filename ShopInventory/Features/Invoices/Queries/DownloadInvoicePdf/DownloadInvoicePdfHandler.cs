using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.DownloadInvoicePdf;

public sealed class DownloadInvoicePdfHandler(
    ISAPServiceLayerClient sapClient,
    IInvoicePdfService invoicePdfService,
    IOptions<SAPSettings> settings,
    ILogger<DownloadInvoicePdfHandler> logger
) : IRequestHandler<DownloadInvoicePdfQuery, ErrorOr<(byte[] PdfBytes, string FileName)>>
{
    public async Task<ErrorOr<(byte[] PdfBytes, string FileName)>> Handle(
        DownloadInvoicePdfQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        try
        {
            var invoice = await sapClient.GetInvoiceByDocEntryAsync(request.DocEntry, cancellationToken);
            if (invoice is null)
                return Errors.Invoice.NotFound(request.DocEntry);

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
                    logger.LogWarning(bpEx, "Could not fetch business partner {CardCode} for PDF enrichment", invoice.CardCode);
                }
            }

            var pdfBytes = await invoicePdfService.GenerateInvoicePdfAsync(invoiceDto);
            var fileName = $"Invoice_{invoiceDto.DocNum}_{DateTime.Now:yyyyMMdd}.pdf";

            return (pdfBytes, fileName);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating PDF for invoice {DocEntry}", request.DocEntry);
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
