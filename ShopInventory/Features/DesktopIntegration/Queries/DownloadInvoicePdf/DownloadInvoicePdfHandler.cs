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
    IFiscalReceiptReader fiscalReceiptReader,
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
            var receipt = await TryLookupFiscalReceiptAsync(invoiceDto.DocNum, cancellationToken);
            fiscalQrCode = receipt?.QrCode;

            // The invoice prints the verification code, day and device beside the QR, so a
            // receipt read from the device must fill them too — not just the QR it answered with.
            if (receipt is { IsFiscalised: true })
            {
                invoiceDto.FiscalVerificationCode ??= receipt.VerificationCode;
                invoiceDto.FiscalDeviceId ??= receipt.DeviceId;
                invoiceDto.FiscalDay ??= receipt.FiscalDay;
                invoiceDto.FiscalReceiptGlobalNo ??= receipt.ReceiptGlobalNo;
            }
        }

        var pdfBytes = await invoicePdfService.GenerateInvoicePdfAsync(invoiceDto, fiscalQrCode);
        var fileName = $"Invoice_{invoiceDto.DocNum}_{DateTime.Now:yyyyMMdd}.pdf";

        return new InvoicePdfResult(pdfBytes, fileName);

        async Task<FiscalReceiptSnapshot?> TryLookupFiscalReceiptAsync(int docNum, CancellationToken token)
        {
            try
            {
                return await fiscalReceiptReader.TryLookupAsync(
                    docNum,
                    ReceiptType.FiscalInvoice,
                    logger,
                    token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not load the fiscal receipt for desktop invoice {DocNum} while generating PDF",
                    docNum);
                return null;
            }
        }
    }
}
