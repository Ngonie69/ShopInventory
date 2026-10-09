using ErrorOr;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Mappings;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices;

/// <summary>
/// Reads a SAP invoice, fills in the customer and the fiscal receipt, and renders the PDF.
/// </summary>
/// <remarks>
/// Lifted out of the download handler unchanged, so that the download and the WhatsApp copy are one
/// code path. The only new input is <c>verifiedReceipt</c>: the WhatsApp sender verifies the receipt
/// against the invoice itself before sending, and prints exactly that, rather than whatever the
/// DocNum-keyed lookups turn up — a DocNum can name another customer's document since SAP reissued
/// numbers after the September 2026 update.
/// </remarks>
public sealed class InvoicePdfComposer(
    ApplicationDbContext dbContext,
    ISAPServiceLayerClient sapClient,
    IFiscalReceiptReader fiscalReceiptReader,
    IInvoicePdfService invoicePdfService,
    IOptions<SAPSettings> settings,
    ILogger<InvoicePdfComposer> logger) : IInvoicePdfComposer
{
    public async Task<ErrorOr<ComposedInvoicePdf>> ComposeAsync(
        int docEntry,
        string? requestedQrCode,
        InvoicePdfReceipt? verifiedReceipt,
        CancellationToken cancellationToken,
        InvoicePdfBuyer? buyer = null)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        try
        {
            var invoice = await sapClient.GetInvoiceByDocEntryAsync(docEntry, cancellationToken);
            if (invoice is null)
                return Errors.Invoice.NotFound(docEntry);

            var invoiceDto = invoice.ToDto();
            await FiscalDocumentStatusProjector.EnrichInvoiceAsync(dbContext, invoiceDto, cancellationToken);

            if (buyer is not null)
            {
                // The shop, not the van's selling account: nothing of the card is the buyer's.
                invoiceDto.CardName = buyer.Name;
                invoiceDto.BillToAddress = buyer.Address;
                invoiceDto.CustomerVatNo = buyer.VatNumber;
                invoiceDto.CustomerTinNumber = null;
                invoiceDto.CustomerPhone = buyer.Phone;
                invoiceDto.CustomerEmail = buyer.Email;
            }
            // Enrich with business partner details
            else if (!string.IsNullOrEmpty(invoice.CardCode))
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

            string? fiscalQrCode;
            if (verifiedReceipt is not null)
            {
                // Printed as verified, all of it: the projection above filled these from whatever row
                // holds this DocNum, which is the very thing the verification was there to distrust.
                invoiceDto.FiscalQrCode = verifiedReceipt.QrCode;
                invoiceDto.FiscalVerificationCode = verifiedReceipt.VerificationCode;
                invoiceDto.FiscalDay = verifiedReceipt.FiscalDay;
                invoiceDto.FiscalDeviceId = verifiedReceipt.DeviceId;
                invoiceDto.FiscalReceiptGlobalNo = verifiedReceipt.ReceiptGlobalNo;
                fiscalQrCode = verifiedReceipt.QrCode;
            }
            else
            {
                fiscalQrCode = await InvoicePdfFiscalDetail.ResolveAsync(
                    dbContext,
                    fiscalReceiptReader,
                    invoiceDto,
                    requestedQrCode,
                    logger,
                    cancellationToken);
            }

            var pdfBytes = await invoicePdfService.GenerateInvoicePdfAsync(invoiceDto, fiscalQrCode);

            return new ComposedInvoicePdf(pdfBytes, invoiceDto, invoice, fiscalQrCode);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating PDF for invoice {DocEntry}", docEntry);
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
