using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.FiscalPrintForms;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>
/// Whether an invoice was filed with ZIMRA as a 48 mm receipt or an A4 invoice, so the copy sent to
/// the customer is the document they were given.
/// </summary>
/// <remarks>
/// <para>
/// The form is not kept on the receipt's local record; it was decided when the sale was filed, by
/// <see cref="IFiscalPrintFormResolver"/> from the sale's channel and the partner's choice on
/// Settings → Fiscalisation. This asks the same question of the same resolver, for the sale the
/// invoice came from — found by the reference SAP keeps on the invoice, the way its receipt is found:
/// </para>
/// <list type="bullet">
/// <item>the sale row (till, vending or van), or</item>
/// <item>the queued invoice, for a van order the office converted.</item>
/// </list>
/// <para>
/// A receipt the handset signed itself was always filed as a receipt, whatever the partner is set to.
/// An invoice with no sale behind it — raised on the web, keyed into SAP — was filed as an A4 invoice,
/// and so is anything whose sale bills a different card: that is not this invoice's sale.
/// </para>
/// </remarks>
internal static class InvoicePrintFormLookup
{
    public static async Task<ReceiptPrintForm> ResolveAsync(
        ApplicationDbContext dbContext,
        IFiscalPrintFormResolver printForms,
        int? desktopSaleId,
        string? saleReference,
        string? cardCode,
        CancellationToken cancellationToken)
    {
        var reference = string.IsNullOrWhiteSpace(saleReference) ? null : saleReference.Trim();
        if (desktopSaleId is null && reference is null)
        {
            return ReceiptPrintForm.InvoiceA4;
        }

        var sales = dbContext.DesktopSales.AsNoTracking();
        sales = desktopSaleId is { } saleId
            ? sales.Where(sale => sale.Id == saleId)
            : sales.Where(sale => sale.ExternalReferenceId == reference);

        var sale = await sales
            .OrderByDescending(sale => sale.Id)
            .Select(sale => new
            {
                sale.SourceSystem,
                sale.CardCode,
                SignedOnHandset = sale.DeviceSignatureValue != null
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (sale is not null)
        {
            if (!SameCard(sale.CardCode, cardCode))
            {
                return ReceiptPrintForm.InvoiceA4;
            }

            return sale.SignedOnHandset
                ? ReceiptPrintForm.Receipt48
                : await printForms.ResolveAsync(sale.SourceSystem, sale.CardCode, cancellationToken);
        }

        if (reference is null)
        {
            return ReceiptPrintForm.InvoiceA4;
        }

        var queuedSource = await dbContext.InvoiceQueue
            .AsNoTracking()
            .Where(queued => queued.ExternalReference == reference)
            .OrderByDescending(queued => queued.Id)
            .Select(queued => queued.SourceSystem)
            .FirstOrDefaultAsync(cancellationToken);

        return queuedSource is null
            ? ReceiptPrintForm.InvoiceA4
            : await printForms.ResolveAsync(queuedSource, cardCode, cancellationToken);
    }

    private static bool SameCard(string? recorded, string? invoice) =>
        string.IsNullOrWhiteSpace(recorded)
        || string.IsNullOrWhiteSpace(invoice)
        || string.Equals(recorded.Trim(), invoice.Trim(), StringComparison.OrdinalIgnoreCase);
}
