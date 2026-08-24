using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Mobile;

/// <summary>
/// Turns the identifier a handset holds into the SAP invoice a POD is filed against.
/// </summary>
/// <remarks>
/// The handset is never given a SAP <c>DocEntry</c>. Its lists carry the platform's own order id, so
/// every POD route has to make this hop, and both of them must make it the same way — a second copy
/// that resolved differently would file a delivery note against a different document than the screen
/// the rep was looking at named.
/// </remarks>
public static class VanSalesPodTarget
{
    /// <summary>
    /// The SAP document entry for <paramref name="legacyOrderId"/>, or null when the id belongs to a
    /// sales order that has not been invoiced yet.
    /// </summary>
    /// <remarks>
    /// A sales order resolves through its invoice, and returns null while it has none — that is the
    /// case a rep walks into, having just delivered against an order. Anything else is looked up as an
    /// invoice, by SAP document entry or by the platform's own id, and falls back to the id itself for
    /// a caller that already holds a document entry. SAP has the last word either way: the caller asks
    /// it for the invoice next, and a number it does not know is refused there.
    /// </remarks>
    public static async Task<int?> ResolveInvoiceDocEntryAsync(
        ApplicationDbContext db,
        int legacyOrderId,
        CancellationToken cancellationToken)
    {
        var salesOrder = await db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Id == legacyOrderId)
            .Select(order => new
            {
                InvoiceSapDocEntry = order.Invoice != null ? order.Invoice.SAPDocEntry : null
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (salesOrder is not null)
        {
            return salesOrder.InvoiceSapDocEntry;
        }

        var invoiceDocEntry = await db.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.SAPDocEntry == legacyOrderId || invoice.Id == legacyOrderId)
            .Select(invoice => invoice.SAPDocEntry)
            .FirstOrDefaultAsync(cancellationToken);

        return invoiceDocEntry > 0 ? invoiceDocEntry : legacyOrderId;
    }
}
