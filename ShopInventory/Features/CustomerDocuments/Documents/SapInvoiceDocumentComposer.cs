using System.Globalization;
using System.Security.Cryptography;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
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
        if (delivery.SapDocEntry is null && delivery.DesktopSaleId is { } saleId)
        {
            var waiting = await AdoptPostedSaleAsync(delivery, saleId, cancellationToken);
            if (waiting is not null)
                return waiting;
        }

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

        var buyer = await ResolveBuyerAsync(delivery, cancellationToken);
        var composed = await pdfComposer.ComposeAsync(docEntry, null, verdict.Receipt, cancellationToken, buyer);
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
            buyer?.Name ?? invoice.CardName,
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

    /// <summary>
    /// A send asked for at a van sale names the sale, because the handset asks before the office has
    /// posted it. Once the sale row carries its SAP numbers the delivery takes them and is an ordinary
    /// invoice send from then on; until then it waits, and is held for a person if it waits too long.
    /// </summary>
    private async Task<DocumentComposition?> AdoptPostedSaleAsync(
        CustomerDocumentDeliveryEntity delivery,
        int saleId,
        CancellationToken cancellationToken)
    {
        var sale = await dbContext.DesktopSales
            .AsNoTracking()
            .Where(row => row.Id == saleId)
            .Select(row => new { row.SapDocEntry, row.SapDocNum, row.CardCode })
            .FirstOrDefaultAsync(cancellationToken);

        if (sale is null)
            return DocumentComposition.Fail("The van sale is no longer on record.");

        if (sale.SapDocEntry is not { } docEntry || sale.SapDocNum is not { } docNum)
            return DocumentComposition.WaitForFiscal("Waiting for the van sale to reach SAP.");

        // The invoice bills the van's card, as the sale did; anything else is not this sale's invoice.
        if (!string.Equals(sale.CardCode?.Trim(), delivery.CardCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            return DocumentComposition.Hold($"The van sale now bills {sale.CardCode}, not {delivery.CardCode} as when it was asked for.");

        delivery.SapDocEntry = docEntry;
        delivery.SapDocNum = docNum;
        delivery.DocumentNumber = docNum.ToString(CultureInfo.InvariantCulture);
        logger.LogInformation(
            "WhatsApp delivery {DeliveryId} for van sale {SaleId} now sends SAP invoice {DocNum}",
            delivery.Id, saleId, docNum);
        return null;
    }

    /// <summary>
    /// The shop a van sale was for, printed as the buyer in place of the van's own card. Null for an
    /// account customer's invoice, which prints its card as it always has.
    /// </summary>
    private async Task<InvoicePdfBuyer?> ResolveBuyerAsync(
        CustomerDocumentDeliveryEntity delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.RouteCustomerId is not { } routeCustomerId)
            return null;

        var shop = await dbContext.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.Id == routeCustomerId)
            .Select(customer => new { customer.Name, customer.Surname, customer.Address, customer.VatNumber, customer.Phone, customer.Email })
            .FirstOrDefaultAsync(cancellationToken);

        if (shop is null)
        {
            return string.IsNullOrWhiteSpace(delivery.RouteCustomerName)
                ? null
                : new InvoicePdfBuyer(delivery.RouteCustomerName.Trim(), null, null, null, null);
        }

        return new InvoicePdfBuyer(
            $"{shop.Name} {shop.Surname}".Trim(),
            Blank(shop.Address),
            Blank(shop.VatNumber),
            Blank(shop.Phone),
            Blank(shop.Email));

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
