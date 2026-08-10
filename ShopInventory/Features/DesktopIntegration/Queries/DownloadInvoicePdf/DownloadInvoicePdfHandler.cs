using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Mappings;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Queries.DownloadInvoicePdf;

public sealed class DownloadInvoicePdfHandler(
    ApplicationDbContext dbContext,
    ISAPServiceLayerClient sapClient,
    IFiscalisationApiClient fiscalisationClient,
    IFiscalDeviceConfigCache fiscalConfigCache,
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
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(dbContext, invoiceDto, cancellationToken);

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

        var fiscalQrCode = query.FiscalQrCode;
        if (string.IsNullOrWhiteSpace(fiscalQrCode))
        {
            fiscalQrCode = invoiceDto.FiscalQrCode;
        }

        if (string.IsNullOrWhiteSpace(fiscalQrCode))
        {
            fiscalQrCode = await TryGetFiscalQrCodeAsync(invoiceDto.DocNum, cancellationToken);
        }

        var pdfBytes = await invoicePdfService.GenerateInvoicePdfAsync(invoiceDto, fiscalQrCode);
        var fileName = $"Invoice_{invoiceDto.DocNum}_{DateTime.Now:yyyyMMdd}.pdf";

        return new InvoicePdfResult(pdfBytes, fileName);

        async Task<string?> TryGetFiscalQrCodeAsync(int docNum, CancellationToken token)
        {
            try
            {
                var snapshot = await FiscalReceiptLookup.TryLookupAsync(
                    fiscalisationClient,
                    fiscalConfigCache,
                    docNum,
                    ReceiptType.FiscalInvoice,
                    logger,
                    token);

                return snapshot?.QrCode;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not load the fiscal QR code for desktop invoice {DocNum} while generating PDF",
                    docNum);
                return null;
            }
        }
    }
}
