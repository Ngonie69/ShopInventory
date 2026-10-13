using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.CustomerDocuments;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The SAP posting date of a credit memo raised against a van, shop or vending invoice: the day that
/// invoice was posted, not the day the credit was raised.
/// </summary>
/// <remarks>
/// These three channels are reconciled a trading day at a time — a shop's takings, a van's load and a
/// vendor's cash are each closed against that day's invoices. A return keyed a day or more later and
/// dated "today" reverses the sale in a day it never belonged to, so both days read wrong. Every other
/// credit note keeps the day it is raised.
/// </remarks>
public static class CreditMemoPostingDate
{
    /// <summary>The day SAP holds the invoice under, or null when SAP's answer carries no readable date.</summary>
    public static DateOnly? OfInvoice(Invoice invoice) =>
        InvoiceDeliveryRules.ParseDocDate(invoice.DocDate) is { } posted
            ? DateOnly.FromDateTime(posted)
            : null;

    /// <summary>
    /// Whether a SAP invoice came from a van, a shop till or vending, going by this database only.
    /// </summary>
    /// <remarks>
    /// Three markers, because the three routes into SAP leave different ones: a sale row that names the
    /// invoice (tills, vending, and van sales that arrived as sales), an end-of-day consolidation of
    /// shop sales, and a van reservation confirmed as the invoice (online van sales and converted
    /// orders — the same test <see cref="VanSaleCreditNotes"/> lists van credit notes by).
    /// </remarks>
    public static async Task<bool> IsVanShopOrVendingInvoiceAsync(
        ApplicationDbContext db, int invoiceDocEntry, CancellationToken cancellationToken)
    {
        if (invoiceDocEntry <= 0)
        {
            return false;
        }

        return await db.DesktopSales.AsNoTracking()
                   .AnyAsync(sale => sale.SapDocEntry == invoiceDocEntry, cancellationToken)
               || await db.SaleConsolidations.AsNoTracking()
                   .AnyAsync(consolidation => consolidation.SapDocEntry == invoiceDocEntry, cancellationToken)
               || await db.StockReservations.AsNoTracking()
                   .AnyAsync(
                       reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                      && reservation.Status == ReservationStatus.Confirmed
                                      && reservation.SAPDocEntry == invoiceDocEntry,
                       cancellationToken);
    }
}
