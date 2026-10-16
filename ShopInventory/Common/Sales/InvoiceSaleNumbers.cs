using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Puts the sale number — <c>INV12830</c> — on the SAP invoices a till sale posted as.
/// </summary>
/// <remarks>
/// The till's invoice history is a list of SAP invoices, and SAP knows nothing of the platform's sale
/// id. Without this the till headed each row with <c>INV</c> and the SAP document entry, which looks
/// exactly like a sale number and is not one: the receipt in the customer's hand says <c>INV12830</c>
/// and the screen said <c>INV2405175</c>. See <see cref="DesktopSaleNumber"/> for the number itself.
///
/// <para>
/// The invoice is tied to its sale by the till's own reference, which SAP carries as
/// <see cref="InvoiceDto.VanSaleOrderNumber"/> and the sales table holds unique and indexed. It is the
/// same key the till reprints a sale by. An invoice no sale posted — raised in the office, or one
/// consolidated invoice for many sales — gets no number, because there is no one sale to name.
/// </para>
/// </remarks>
public static class InvoiceSaleNumbers
{
    public static async Task StampAsync(ApplicationDbContext db, IReadOnlyCollection<InvoiceDto> invoices, CancellationToken ct)
    {
        var references = invoices
            .Select(invoice => invoice.VanSaleOrderNumber?.Trim())
            .Where(reference => !string.IsNullOrEmpty(reference))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (references.Count == 0)
            return;

        var saleIds = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => references.Contains(sale.ExternalReferenceId))
            .Select(sale => new { sale.ExternalReferenceId, sale.Id })
            .ToListAsync(ct);

        var byReference = saleIds.ToDictionary(
            sale => sale.ExternalReferenceId, sale => sale.Id, StringComparer.Ordinal);

        foreach (var invoice in invoices)
        {
            if (invoice.VanSaleOrderNumber?.Trim() is { Length: > 0 } reference
                && byReference.TryGetValue(reference, out var saleId))
            {
                invoice.SaleNumber = DesktopSaleNumber.Format(saleId);
            }
        }
    }
}
