using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.DownloadCreditNotePdf;

/// <summary>
/// Reads a credit memo from SAP and prints it on the invoice's sheet, fiscal receipt included.
/// </summary>
/// <remarks>
/// The receipt comes from <see cref="CreditNoteReceiptLookup"/>, which accepts a fiscal log row only
/// when it is this memo's account's — never the device by DocNum alone, the last resort the invoice
/// PDF falls back on, because a reissued DocNum there can print another customer's QR.
/// </remarks>
public sealed class DownloadCreditNotePdfHandler(
    ApplicationDbContext dbContext,
    ISAPServiceLayerClient sapClient,
    IInvoicePdfService pdfService,
    IOptions<SAPSettings> settings,
    ILogger<DownloadCreditNotePdfHandler> logger
) : IRequestHandler<DownloadCreditNotePdfQuery, ErrorOr<CreditNotePdfFile>>
{
    public async Task<ErrorOr<CreditNotePdfFile>> Handle(
        DownloadCreditNotePdfQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
        {
            return Errors.CreditNote.SapDisabled;
        }

        SAPCreditNote? creditNote;
        try
        {
            creditNote = await sapClient.GetCreditNoteByDocEntryAsync(request.DocEntry, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read credit note {DocEntry} from SAP for its PDF", request.DocEntry);
            return Errors.CreditNote.SapReadFailed(ex.Message);
        }

        if (creditNote is null)
        {
            return Errors.CreditNote.NotFound(request.DocEntry);
        }

        if (request.CardCode is { } expectedCardCode)
        {
            // Another account's memo, or one that was cancelled: to a customer, neither exists. Both
            // answer as not found, so the reply says nothing about a document that is not theirs.
            if (!string.Equals(creditNote.CardCode?.Trim(), expectedCardCode.Trim(), StringComparison.OrdinalIgnoreCase)
                || CreditNoteFigures.IsCancelledOrCancellation(creditNote))
            {
                logger.LogWarning(
                    "Refused the PDF of credit note {DocEntry} to a caller expecting account {CardCode}",
                    request.DocEntry, expectedCardCode);
                return Errors.CreditNote.NotFound(request.DocEntry);
            }
        }

        var receipts = await CreditNoteReceiptLookup.FindAsync(dbContext, [creditNote], cancellationToken);
        receipts.TryGetValue(creditNote.DocEntry, out var receipt);

        var document = CreditNotePdfDocument.From(creditNote, receipt);
        await AddCustomerDetailsAsync(document, cancellationToken);

        var details = new CreditNotePdfDetails(
            await CreditedInvoiceNumberAsync(creditNote, cancellationToken),
            CreditNoteFigures.Reason(creditNote));

        try
        {
            var pdf = await pdfService.GenerateCreditNotePdfAsync(document, details, receipt?.QrCode);
            return new CreditNotePdfFile(pdf, $"CreditNote_{creditNote.DocNum}.pdf");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not render the PDF of credit note {DocEntry}", request.DocEntry);
            return Errors.CreditNote.PdfFailed(request.DocEntry);
        }
    }

    /// <summary>The customer's VAT and TIN numbers and contacts, which the sheet prints in its address box.</summary>
    private async Task AddCustomerDetailsAsync(DTOs.InvoiceDto document, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(document.CardCode))
        {
            return;
        }

        try
        {
            var partner = await sapClient.GetBusinessPartnerByCodeAsync(document.CardCode, cancellationToken);
            if (partner is not null)
            {
                document.CustomerVatNo = partner.VatRegNo;
                document.CustomerTinNumber = partner.TinNumber;
                document.CustomerPhone = partner.Phone1;
                document.CustomerEmail = partner.Email;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read business partner {CardCode} for a credit note PDF", document.CardCode);
        }
    }

    /// <summary>The number of the invoice the memo credits, when it is on the memo's own account; otherwise 0.</summary>
    private async Task<int> CreditedInvoiceNumberAsync(SAPCreditNote creditNote, CancellationToken cancellationToken)
    {
        if (CreditNoteFigures.CreditedInvoiceDocEntry(creditNote) is not { } invoiceDocEntry || invoiceDocEntry <= 0)
        {
            return 0;
        }

        try
        {
            var invoice = (await sapClient.GetInvoiceBalancesByDocEntriesAsync([invoiceDocEntry], cancellationToken))
                .FirstOrDefault(candidate => candidate.DocEntry == invoiceDocEntry);

            return invoice is not null
                && string.Equals(invoice.CardCode?.Trim(), creditNote.CardCode?.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? invoice.DocNum
                    : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read invoice {InvoiceDocEntry} for the PDF of credit note {DocEntry}",
                invoiceDocEntry, creditNote.DocEntry);
            return 0;
        }
    }
}
