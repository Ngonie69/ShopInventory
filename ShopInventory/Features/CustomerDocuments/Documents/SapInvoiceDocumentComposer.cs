using System.Security.Cryptography;
using ErrorOr;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Invoices;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>
/// Prepares a SAP invoice for a customer's WhatsApp: the receipt is verified first, from local records,
/// and the invoice is read from SAP and rendered only once there is one to print.
/// </summary>
/// <remarks>
/// A tax invoice without its fiscal block is not the document the customer is owed, so nothing is
/// sent until the receipt is found — and found to be this invoice's (see <see cref="FiscalLinkVerifier"/>).
/// Waiting costs no SAP reads: the check runs on the facts the delivery copied when it was queued.
/// </remarks>
public sealed class SapInvoiceDocumentComposer(
    ApplicationDbContext dbContext,
    IFiscalReceiptReader fiscalReceiptReader,
    IInvoicePdfComposer pdfComposer,
    IOptions<CustomerDocumentDeliverySettings> options,
    IOptions<FiscalisationSettings> fiscalisationOptions,
    ILogger<SapInvoiceDocumentComposer> logger) : ISapInvoiceDocumentComposer
{
    public async Task<DocumentComposition> ComposeAsync(
        CustomerDocumentDeliveryEntity delivery,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (delivery.SapDocEntry is not { } docEntry || delivery.SapDocNum is not { } docNum)
        {
            return DocumentComposition.Fail("The delivery names no SAP invoice.");
        }

        var settings = options.Value;
        var deviceLookupAllowed = nowUtc - delivery.CreatedAtUtc >= TimeSpan.FromMinutes(Math.Max(0, settings.DeviceLookupAfterMinutes));

        var verdict = await FiscalLinkVerifier.VerifyAsync(
            dbContext,
            fiscalReceiptReader,
            new FiscalLinkQuery(
                docNum,
                delivery.CardCode,
                delivery.DocumentTotal,
                delivery.DocumentTotalFc,
                delivery.DocumentDate,
                delivery.SaleReference,
                delivery.CreatedAtUtc),
            deviceLookupAllowed,
            logger,
            cancellationToken);

        switch (verdict.Kind)
        {
            case FiscalLinkVerdictKind.NotYet:
                return DocumentComposition.WaitForFiscal(verdict.Reason ?? "The invoice has no fiscal receipt yet.");
            case FiscalLinkVerdictKind.Mismatch:
                return DocumentComposition.Hold(verdict.Reason ?? "The fiscal receipt found does not belong to this invoice.");
        }

        var composed = await pdfComposer.ComposeAsync(docEntry, null, verdict.Receipt, cancellationToken);
        if (composed.IsError)
        {
            return composed.FirstError.Type == ErrorType.NotFound
                ? DocumentComposition.Fail($"Invoice {docNum} is no longer in SAP.")
                : DocumentComposition.Retry($"The invoice could not be read from SAP: {composed.FirstError.Description}");
        }

        var sapInvoice = composed.Value.SapInvoice;

        // A DocEntry is SAP's own key, but SAP reissued DocEntries after the September 2026 update. The
        // delivery copied who the invoice was for when it was queued; a document that now answers to
        // the same key with another number or customer is not the one that was asked for.
        if (sapInvoice.DocNum != docNum
            || !string.Equals(sapInvoice.CardCode?.Trim(), delivery.CardCode?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return DocumentComposition.Hold(
                $"SAP's invoice {docEntry} is now number {sapInvoice.DocNum} for {sapInvoice.CardCode}, not the invoice {docNum} for {delivery.CardCode} that was queued.");
        }

        if (InvoiceDeliveryRules.IsCancelled(sapInvoice))
        {
            return DocumentComposition.Cancel($"Invoice {docNum} was cancelled in SAP before it was sent.");
        }

        if (InvoiceDeliveryRules.IsReposted(fiscalisationOptions.Value, sapInvoice))
        {
            return DocumentComposition.Cancel($"Invoice {docNum} is a repost after the SAP update; its receipt is under its old number.");
        }

        var bytes = composed.Value.PdfBytes;
        if (bytes.Length > settings.MaxDocumentBytes)
        {
            return DocumentComposition.Fail(
                $"The PDF is {bytes.Length / 1024:N0} KB, more than the {settings.MaxDocumentBytes / 1024:N0} KB allowed.");
        }

        var invoice = composed.Value.Invoice;
        var number = invoice.DocNum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var caption = CustomerDocumentCaption.Render(
            settings.CaptionTemplate,
            invoice.CardName,
            number,
            InvoiceDeliveryRules.ParseDocDate(invoice.DocDate),
            invoice.DocCurrency,
            InvoiceDeliveryRules.DisplayTotal(invoice.DocTotal, InvoiceDeliveryRules.ForeignTotal(sapInvoice)));

        return DocumentComposition.Ready(new ComposedCustomerDocument(
            bytes,
            CustomerDocumentCaption.FileName(settings.FileNameTemplate, number),
            caption,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            verdict.Receipt!,
            verdict.Source ?? FiscalLinkVerifier.TransactionLogSource));
    }
}
